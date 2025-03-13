using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using VDS.RDF;
using VDS.RDF.Parsing;
using VDS.RDF.Nodes;
using Microsoft.OpenApi.Models;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Extensions;
using Microsoft.OpenApi;
using Lucene.Net.Index;

namespace OwlToOpenApi
{
  /// <summary>
  /// Convertitore OWL (in Turtle) -> OpenAPI 3.0.
  /// 
  /// Legge il file "reconstruction.ttl" che rappresenta l'ontologia e genera un OpenApiDocument.
  /// Durante la conversione dei components/schemas, vengono lette le annotazioni:
  /// - "field_string" per i campi semplici (string).
  /// - "field_stringArray" per i campi array di stringhe.
  /// - "field_required" per indicare i campi obbligatori.
  /// Inoltre, per le proprietà complesse definite tramite relazioni "has*":
  /// - Se il nome dello schema referenziato segue il pattern "{nome_classe}_data" o "{nome_classe}_results",
  ///   la proprietà viene mappata rispettivamente come "data" o "results" e come array.
  /// - Altrimenti, la proprietà viene inserita direttamente con il nome dello schema referenziato.
  /// Infine, la funzione InlineNestedSchemas incapsula i riferimenti ($ref) all’interno dei components.
  /// </summary>
  public class OwlToOpenApiConverter
  {
    private readonly IGraph _graph;
    // Dizionario per salvare i nodi schema già elaborati (chiave = local name)
    private readonly Dictionary<string, OpenApiSchema> _processedSchemas = new Dictionary<string, OpenApiSchema>();

    // Costruttore: carica il file TTL in un grafo RDF.
    public OwlToOpenApiConverter(string ttlPath)
    {
      _graph = new Graph();
      _graph.LoadFromFile(ttlPath);
    }

    /// <summary>
    /// Esegue la conversione e produce un OpenApiDocument completo.
    /// </summary>
    public OpenApiDocument Convert()
    {
      var doc = new OpenApiDocument
      {
        Info = new OpenApiInfo(),
        Servers = new List<OpenApiServer>(),
        Paths = new OpenApiPaths(),
        Components = new OpenApiComponents()
      };

      ParseInfo(doc);
      ParseServers(doc);
      ParsePaths(doc);
      ParseSchemas(doc);
      InlineNestedSchemas(doc);

      return doc;
    }

    /// <summary>
    /// Serializza l'OpenApiDocument su file JSON.
    /// </summary>
    public void WriteToFile(OpenApiDocument doc, string outJsonPath)
    {
      var openApiJson = doc.SerializeAsJson(OpenApiSpecVersion.OpenApi3_0);
      File.WriteAllText(outJsonPath, openApiJson);
    }

    #region Parse Info e Servers

    private void ParseInfo(OpenApiDocument doc)
    {
      var lifecycleClass = GetUriNode(":Lifecycle");
      if (lifecycleClass != null)
      {
        string openapiVer = GetLiteralObject(lifecycleClass, ":openapi");
        if (!string.IsNullOrEmpty(openapiVer))
          doc.Info.Version = openapiVer;
      }
      var infoClass = GetUriNode(":info");
      if (infoClass != null)
      {
        doc.Info.Title = GetLiteralObject(infoClass, ":title") ?? "Default Title";
        doc.Info.Version = GetLiteralObject(infoClass, ":version") ?? "1.0.0";
        doc.Info.Description = GetLiteralObject(infoClass, ":description");

        var dsService = GetLiteralObject(infoClass, ":x-ds-service");
        if (!string.IsNullOrEmpty(dsService))
        {
          var splitted = dsService.Split(',');
          var arr = new OpenApiArray();
          foreach (var s in splitted)
            arr.Add(new OpenApiString(s.Trim()));
          doc.Info.Extensions["x-ds-service"] = arr;
        }
        var dsCopyright = GetLiteralObject(infoClass, ":x-ds-copyright");
        if (!string.IsNullOrEmpty(dsCopyright))
          doc.Info.Extensions["x-ds-copyright"] = new OpenApiString(dsCopyright);
      }
    }

    private void ParseServers(OpenApiDocument doc)
    {
      var serversClass = GetUriNode(":servers");
      if (serversClass == null) return;
      var subClassOf = GetUriNode("rdfs:subClassOf");
      var serverNodes = _graph.GetTriplesWithPredicateObject(subClassOf, serversClass)
                              .Select(t => t.Subject)
                              .Distinct()
                              .ToList();
      foreach (var sNode in serverNodes)
      {
        string url = GetLiteralObject(sNode, ":url");
        if (!string.IsNullOrEmpty(url))
          doc.Servers.Add(new OpenApiServer { Url = url });
      }
    }

    #endregion

    #region Parse Paths

    private void ParsePaths(OpenApiDocument doc)
    {
      var pathsClass = GetUriNode(":paths");
      if (pathsClass == null) return;
      var subClassOf = GetUriNode("rdfs:subClassOf");
      var pathNodes = _graph.GetTriplesWithPredicateObject(subClassOf, pathsClass)
                            .Select(t => t.Subject)
                            .Distinct()
                            .ToList();
      foreach (var pathNode in pathNodes)
      {
        string pathUrl = GetLiteralObject(pathNode, ":path");
        if (string.IsNullOrEmpty(pathUrl)) continue;
        if (!doc.Paths.ContainsKey(pathUrl))
          doc.Paths[pathUrl] = new OpenApiPathItem();
        var operationType = ParseOperationType(GetLiteralObject(pathNode, ":method") ?? "get");
        var summary = GetLiteralObject(pathNode, ":summary");
        var desc = GetLiteralObject(pathNode, ":description");
        var opId = GetLiteralObject(pathNode, ":operationId");
        var tagsStr = GetLiteralObject(pathNode, ":tags");
        var operation = new OpenApiOperation
        {
          Summary = summary,
          Description = desc,
          OperationId = opId,
          Tags = new List<OpenApiTag>(),
          Parameters = new List<OpenApiParameter>(),
          Responses = new OpenApiResponses()
        };
        if (!string.IsNullOrEmpty(tagsStr))
        {
          foreach (var t in tagsStr.Split(','))
            operation.Tags.Add(new OpenApiTag { Name = t.Trim() });
        }
        ParseHeadersForPath(pathNode, operation);
        ParseRequestBodyForPath(pathNode, operation);
        ParseResponsesForPath(pathNode, operation);
        doc.Paths[pathUrl].Operations[operationType] = operation;
      }
    }

    private void ParseHeadersForPath(INode pathNode, OpenApiOperation operation)
    {
      var hasHeaderPred = GetUriNode(":hasHeader");
      var headerTriples = _graph.GetTriplesWithSubjectPredicate(pathNode, hasHeaderPred).ToList();
      foreach (var triple in headerTriples)
      {
        var headerClass = triple.Object;
        if (headerClass == null) continue;
        var fieldString = GetLiteralObject(headerClass, ":field_string");
        var fieldBool = GetLiteralObject(headerClass, ":field_bool");
        var fieldInt = GetLiteralObject(headerClass, ":field_integer");
        var fieldReq = GetLiteralObject(headerClass, ":field_required");
        if (!string.IsNullOrEmpty(fieldString))
        {
          foreach (var token in fieldString.Split(','))
          {
            var parts = token.Split(':');
            string name = parts[0].Trim();
            bool required = IsFieldRequired(fieldReq, name);
            operation.Parameters.Add(MakeParam(name, "string", required));
          }
        }
        if (!string.IsNullOrEmpty(fieldBool))
        {
          foreach (var token in fieldBool.Split(','))
          {
            var parts = token.Split(':');
            string name = parts[0].Trim();
            bool required = IsFieldRequired(fieldReq, name);
            operation.Parameters.Add(MakeParam(name, "boolean", required));
          }
        }
        if (!string.IsNullOrEmpty(fieldInt))
        {
          foreach (var token in fieldInt.Split(','))
          {
            var parts = token.Split(':');
            string name = parts[0].Trim();
            bool required = IsFieldRequired(fieldReq, name);
            operation.Parameters.Add(MakeParam(name, "integer", required));
          }
        }
      }
    }

    private void ParseRequestBodyForPath(INode pathNode, OpenApiOperation operation)
    {
      var hasRequestBodyPred = GetUriNode(":hasRequestBody");
      var tlist = _graph.GetTriplesWithSubjectPredicate(pathNode, hasRequestBodyPred).ToList();
      if (tlist.Count == 0) return;
      var schemaNode = tlist.First().Object;
      string schemaName = NodeToLocalName(schemaNode);
      if (string.IsNullOrEmpty(schemaName)) return;
      // Manteniamo il $ref per il requestBody.
      operation.RequestBody = new OpenApiRequestBody
      {
        Required = true,
        Content = new Dictionary<string, OpenApiMediaType>
        {
          ["application/json"] = new OpenApiMediaType
          {
            Schema = new OpenApiSchema
            {
              Reference = new OpenApiReference { Id = schemaName, Type = ReferenceType.Schema }
            }
          }
        }
      };
    }

    private void ParseResponsesForPath(INode pathNode, OpenApiOperation operation)
    {
      var hasResponse = GetUriNode(":hasResponse");
      var respTriples = _graph.GetTriplesWithSubjectPredicate(pathNode, hasResponse);
      bool found200 = false;
      foreach (var tr in respTriples)
      {
        found200 = true;
        var schemaNode = tr.Object;
        string schemaName = NodeToLocalName(schemaNode);
        operation.Responses["200"] = new OpenApiResponse
        {
          Description = "Operation completed successfully.",
          Content = new Dictionary<string, OpenApiMediaType>
          {
            ["application/json"] = new OpenApiMediaType
            {
              Schema = new OpenApiSchema
              {
                Reference = new OpenApiReference { Id = schemaName, Type = ReferenceType.Schema }
              }
            }
          }
        };
      }
      if (!found200)
      {
        operation.Responses["200"] = new OpenApiResponse
        {
          Description = "No standard output found (Empty)."
        };
      }
      var hasError = GetUriNode(":hasError");
      var errorTriples = _graph.GetTriplesWithSubjectPredicate(pathNode, hasError);
      foreach (var tr in errorTriples)
      {
        var errorSchemaNode = tr.Object;
        string errorSchemaName = NodeToLocalName(errorSchemaNode);
        string code = "400";
        if (errorSchemaName.Contains("401"))
          code = "401";
        else if (errorSchemaName.Contains("403"))
          code = "403";
        else if (errorSchemaName.Contains("404"))
          code = "404";
        else if (errorSchemaName.Contains("500"))
          code = "500";
        operation.Responses[code] = new OpenApiResponse
        {
          Description = "Error " + code,
          Content = new Dictionary<string, OpenApiMediaType>
          {
            ["application/json"] = new OpenApiMediaType
            {
              Schema = new OpenApiSchema
              {
                Reference = new OpenApiReference { Id = errorSchemaName, Type = ReferenceType.Schema }
              }
            }
          }
        };
      }
    }

    private OperationType ParseOperationType(string method)
    {
      switch (method.ToLowerInvariant())
      {
        case "get": return OperationType.Get;
        case "post": return OperationType.Post;
        case "put": return OperationType.Put;
        case "delete": return OperationType.Delete;
        case "patch": return OperationType.Patch;
        case "head": return OperationType.Head;
        case "options": return OperationType.Options;
        default: return OperationType.Get;
      }
    }

    #endregion

    #region Parse Schemas

    /// <summary>
    /// Effettua il parsing dei nodi OWL per generare i components/schemas.
    /// Legge le annotazioni "field_string", "field_stringArray" e "field_required".
    /// </summary>
    private void ParseSchemas(OpenApiDocument doc)
    {
      ProcessSchemaNodesForClass(doc, ":schemas");
      ProcessSchemaNodesForClass(doc, ":Errors");
      ProcessSchemaNodesForClass(doc, ":data");
    }

    private void ProcessSchemaNodesForClass(OpenApiDocument doc, string parentClassQName)
    {
      var parentNode = GetUriNode(parentClassQName);
      if (parentNode == null) return;
      var subClassOf = GetUriNode("rdfs:subClassOf");
      var schemaNodes = _graph.GetTriplesWithPredicateObject(subClassOf, parentNode)
                              .Select(t => t.Subject)
                              .Distinct()
                              .ToList();
      foreach (var sNode in schemaNodes)
      {
        string schemaName = NodeToLocalName(sNode);

        //schemaName = char.ToLower(schemaName[0]) + schemaName.Substring(1);
        if (string.IsNullOrEmpty(schemaName))
          continue;
        if (!_processedSchemas.ContainsKey(schemaName))
        {
          var schema = ParseSchemaNode(sNode);
          _processedSchemas[schemaName] = schema;
        }
        if (!doc.Components.Schemas.ContainsKey(schemaName)) {
          string camelCseSchemaNameOnNonErrorClass = schemaName;
          if (!schemaName.Contains("Error")){
            camelCseSchemaNameOnNonErrorClass = char.ToLower(schemaName[0]) + schemaName.Substring(1);
          }
          doc.Components.Schemas[schemaName] = _processedSchemas[schemaName];
        }
      }
    }

    private OpenApiSchema ParseSchemaNode(INode sNode)
    {
      var schema = new OpenApiSchema
      {
        Type = "object",
        Properties = new Dictionary<string, OpenApiSchema>(),
        Required = new HashSet<string>()
      };

      // (1) Campi semplici: "field_string" e "field_required"
      string fieldString = GetLiteralObject(sNode, ":field_string");
      string fieldRequired = GetLiteralObject(sNode, ":field_required");
      if (!string.IsNullOrEmpty(fieldString))
      {
        foreach (var field in fieldString.Split(',').Select(f => f.Trim()).Where(f => !string.IsNullOrEmpty(f)))
        {
          schema.Properties[field] = new OpenApiSchema { Type = "string" };
          if (!string.IsNullOrEmpty(fieldRequired))
          {
            var reqList = fieldRequired.Split(',').Select(r => r.Trim());
            if (reqList.Contains(field))
              schema.Required.Add(field);
          }
        }
      }
      // (2) Campi array di stringhe: "field_stringArray"
      string fieldStringArray = GetLiteralObject(sNode, ":field_stringArray");
      if (!string.IsNullOrEmpty(fieldStringArray))
      {
        foreach (var field in fieldStringArray.Split(',').Select(f => f.Trim()).Where(f => !string.IsNullOrEmpty(f)))
        {
          schema.Properties[field] = new OpenApiSchema
          {
            Type = "array",
            Items = new OpenApiSchema { Type = "string" }
          };
          if (!string.IsNullOrEmpty(fieldRequired))
          {
            var reqList = fieldRequired.Split(',').Select(r => r.Trim());
            if (reqList.Contains(field))
              schema.Required.Add(field);
          }
        }
      }
      // (3) Proprietà complesse tramite predicati "has*"
      // Legge l'annotazione "field_array" e normalizza in minuscolo
      string fieldArray = GetLiteralObject(sNode, ":field_array");
      HashSet<string> arrayProperties = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      if (!string.IsNullOrEmpty(fieldArray))
      {
        arrayProperties = new HashSet<string>(fieldArray.Split(',').Select(s => s.Trim().ToLowerInvariant()));
      }
      var objectTriples = _graph.GetTriplesWithSubject(sNode)
                                .Where(t => t.Predicate is IUriNode p && p.Uri.ToString().Contains("has"))
                                .ToList();
      foreach (var triple in objectTriples)
      {
        var predUri = ((IUriNode)triple.Predicate).Uri;
        string predLocal = predUri.Fragment.TrimStart('#');
        if (!predLocal.StartsWith("has"))
          continue;
        string nestedSchemaName = NodeToLocalName(triple.Object);
        if (string.IsNullOrEmpty(nestedSchemaName))
          continue;
        string propertyName;
        // Se il nome dello schema referenziato termina con "_data" o "_results", mappa come array con nome "data" o "results"
        if (nestedSchemaName.EndsWith("_data", StringComparison.OrdinalIgnoreCase))
        {
          propertyName = "data";
          schema.Properties[propertyName] = new OpenApiSchema
          {
            Type = "array",
            Items = new OpenApiSchema
            {
              Reference = new OpenApiReference { Id = nestedSchemaName, Type = ReferenceType.Schema }
            }
          };
        }
        else if (nestedSchemaName.EndsWith("_results", StringComparison.OrdinalIgnoreCase))
        {
          propertyName = "results";
          schema.Properties[propertyName] = new OpenApiSchema
          {
            Type = "array",
            Items = new OpenApiSchema
            {
              Reference = new OpenApiReference { Id = nestedSchemaName, Type = ReferenceType.Schema }
            }
          };
        }
        else
        {
          // Altrimenti, se il nome (normalizzato) è presente nell'annotazione "field_array",
          // mappa la proprietà come array, altrimenti come oggetto singolo.
          propertyName = nestedSchemaName;
          if (arrayProperties.Contains(propertyName.ToLowerInvariant()))
          {
            schema.Properties[propertyName] = new OpenApiSchema
            {
              Type = "array",
              Items = new OpenApiSchema
              {
                Reference = new OpenApiReference { Id = nestedSchemaName, Type = ReferenceType.Schema }
              }
            };
          }
          else
          {
            schema.Properties[propertyName] = new OpenApiSchema
            {
              Reference = new OpenApiReference { Id = nestedSchemaName, Type = ReferenceType.Schema }
            };
          }
        }
      }
      return schema;
    }

    #endregion

    #region Inline Nested Schemas

    /// <summary>
    /// Per ogni schema definito in components, incapsula (inline) le strutture nidificate:
    /// se una proprietà ha un $ref e lo schema referenziato è definito in components, 
    /// sostituisce il riferimento col contenuto effettivo in modo ricorsivo.
    /// </summary>
    private void InlineNestedSchemas(OpenApiDocument doc)
    {
      foreach (var key in doc.Components.Schemas.Keys.ToList())
      {
        doc.Components.Schemas[key] = InlineSchema(doc.Components.Schemas[key], doc.Components.Schemas);
      }
    }

    /// <summary>
    /// Inlines ricorsivamente le proprietà di uno schema.
    /// Se una proprietà ha un $ref e lo schema referenziato è presente, il contenuto viene inlined.
    /// </summary>
    private OpenApiSchema InlineSchema(OpenApiSchema schema, IDictionary<string, OpenApiSchema> allSchemas)
    {
      if (schema == null)
        return null;
      if (schema.Reference != null)
      {
        string refId = schema.Reference.Id;
        if (allSchemas.TryGetValue(refId, out var referencedSchema))
        {
          return InlineSchema(referencedSchema, allSchemas);
        }
      }
      if (schema.Properties != null)
      {
        foreach (var key in schema.Properties.Keys.ToList())
        {
          schema.Properties[key] = InlineSchema(schema.Properties[key], allSchemas);
        }
      }
      if (schema.Type == "array" && schema.Items != null)
      {
        schema.Items = InlineSchema(schema.Items, allSchemas);
      }
      return schema;
    }

    #endregion

    #region Helper Methods

    private IUriNode GetUriNode(string qname)
    {
      return _graph.CreateUriNode(qname);
    }

    private string GetLiteralObject(INode subject, string predicateQname)
    {
      var pred = GetUriNode(predicateQname);
      if (pred == null) return null;
      var triple = _graph.GetTriplesWithSubjectPredicate(subject, pred).FirstOrDefault();
      if (triple?.Object is ILiteralNode lit)
        return lit.Value;
      return null;
    }

    private bool IsFieldRequired(string requiredFields, string fieldName)
    {
      if (string.IsNullOrEmpty(requiredFields))
        return false;
      var splitted = requiredFields.Split(',');
      return splitted.Any(x => x.Trim().Equals(fieldName, StringComparison.OrdinalIgnoreCase));
    }

    private OpenApiParameter MakeParam(string paramName, string paramType, bool required)
    {
      return new OpenApiParameter
      {
        Name = paramName,
        In = ParameterLocation.Header,
        Required = required,
        Schema = new OpenApiSchema { Type = paramType }
      };
    }


    private string NodeToLocalName(INode node)
    {
      if (node is IUriNode uriNode)
      {
        string uri = uriNode.Uri.ToString();
        int idx = uri.LastIndexOf('#');
        if (idx >= 0 && idx < uri.Length - 1)
          return uri.Substring(idx + 1);
        idx = uri.LastIndexOf('/');
        if (idx >= 0 && idx < uri.Length - 1)
          return uri.Substring(idx + 1);
        return uri;
      }
      else if (node is IBlankNode blankNode)
      {
        return "bnode_" + blankNode.InternalID;
      }
      return "";
    }

    #endregion
  }

  /// <summary>
  /// Programma principale che utilizza OwlToOpenApiConverter e scrive su disco un file JSON.
  /// </summary>
  internal static class Program
  {
    static void Main()
    {
      string inputTtl = @"C:\Users\mrday\desktop\reconstruction.ttl";
      string outputJson = @"C:\Users\mrday\desktop\reconstructed_lifecycle.json";
      if (!File.Exists(inputTtl))
      {
        Console.WriteLine("File TTL non trovato: " + inputTtl);
        return;
      }
      try
      {
        var converter = new OwlToOpenApiConverter(inputTtl);
        var doc = converter.Convert();
        converter.WriteToFile(doc, outputJson);
        Console.WriteLine("Conversione completata. File generato: " + outputJson);
      }
      catch (Exception ex)
      {
        Console.WriteLine("ERRORE durante la conversione: " + ex.Message);
      }
    }
  }
}
