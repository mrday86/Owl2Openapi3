using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using Microsoft.OpenApi.Writers;
using VDS.RDF;
using VDS.RDF.Nodes;
using VDS.RDF.Parsing;

namespace OwlToOpenApiFull
{
  /// <summary>
  /// Programma console di esempio:
  /// 1) Legge il file Turtle (.ttl) generato dal forward converter.
  /// 2) Ricostruisce un oggetto OpenApiDocument con tutti i dettagli (Info, Servers, Paths, Components.Schemas).
  /// 3) Serializza l’OpenApiDocument in JSON (OpenAPI 3.0).
  /// </summary>
  class Program
  {
    static void Main(string[] args)
    {
      try
      {
        // Percorsi di default (modifica secondo necessità)
        string inputTtlPath = args.Length > 0 ? args[0] : "C:\\Users\\MrDay\\Desktop\\tesi\\out_lifecycle_ontology.ttl";
        string outputJsonPath = args.Length > 1 ? args[1] : "C:\\Users\\MrDay\\Desktop\\tesi\\out_lifecycle_openapi.json";

        var converter = new TtlToOpenApiConverter("http://example.org/ontology#");
        string openApiJson = converter.ConvertTtlToOpenApiJson(inputTtlPath);

        File.WriteAllText(outputJsonPath, openApiJson);

        Console.WriteLine("Conversione OWL -> OpenAPI completata con successo!");
        Console.WriteLine($"File generato: {outputJsonPath}");
      }
      catch (Exception ex)
      {
        Console.WriteLine("Errore: " + ex.Message);
      }
    }
  }

  /// <summary>
  /// Classe che ricostruisce l'oggetto OpenApiDocument da un grafo RDF (in formato Turtle)
  /// generato dal forward converter. Gestisce Info, Servers, Paths/Operations e Components.Schemas.
  /// </summary>
  public class TtlToOpenApiConverter
  {
    private readonly string _baseNs;

    public TtlToOpenApiConverter(string baseNamespace)
    {
      if (!baseNamespace.EndsWith("#")) baseNamespace += "#";
      _baseNs = baseNamespace;
    }

    /// <summary>
    /// Legge il file Turtle, ricostruisce l'OpenApiDocument e lo serializza in JSON.
    /// </summary>
    public string ConvertTtlToOpenApiJson(string ttlPath)
    {
      IGraph g = new Graph();
      g.LoadFromFile(ttlPath);

      var doc = new OpenApiDocument
      {
        Info = new OpenApiInfo(),
        Servers = new List<OpenApiServer>(),
        Paths = new OpenApiPaths(),
        Components = new OpenApiComponents
        {
          Schemas = new Dictionary<string, OpenApiSchema>()
        }
      };

      ReconstructInfo(g, doc);
      ReconstructServers(g, doc);
      ReconstructOperations(g, doc);
      ReconstructSchemas(g, doc);

      using var sw = new StringWriter();
      var writer = new OpenApiJsonWriter(sw);
      doc.SerializeAsV3(writer);
      return sw.ToString();
    }

    #region INFO & SERVERS
    private void ReconstructInfo(IGraph g, OpenApiDocument doc)
    {
      var infoNode = g.CreateUriNode(UriFor("ApiInfo"));
      doc.Info.Title = GetLiteral(g, infoNode, "infoTitle");
      doc.Info.Version = GetLiteral(g, infoNode, "infoVersion");
      doc.Info.Description = GetLiteral(g, infoNode, "infoDescription");
    }

    private void ReconstructServers(IGraph g, OpenApiDocument doc)
    {
      var serverClass = g.CreateUriNode(UriFor("Server"));
      var servers = g.GetTriplesWithPredicateObject(g.CreateUriNode("rdf:type"), serverClass)
                     .Select(t => t.Subject)
                     .Distinct()
                     .ToList();
      foreach (var sNode in servers)
      {
        string url = GetLiteral(g, sNode, "serverUrl");
        string desc = GetLiteral(g, sNode, "serverDescription");
        if (!string.IsNullOrEmpty(url))
          doc.Servers.Add(new OpenApiServer { Url = url, Description = desc });
      }
    }
    #endregion

    #region PATHS & OPERATIONS
    private void ReconstructOperations(IGraph g, OpenApiDocument doc)
    {
      var owlClass = g.CreateUriNode("owl:Class");
      var allClasses = g.GetTriplesWithPredicateObject(g.CreateUriNode("rdf:type"), owlClass)
                        .Select(t => t.Subject)
                        .Distinct()
                        .ToList();
      foreach (var cNode in allClasses)
      {
        var pathVal = GetLiteral(g, cNode, "originalPath");
        if (string.IsNullOrEmpty(pathVal))
          continue;
        var verbVal = GetLiteral(g, cNode, "httpVerb");
        if (string.IsNullOrEmpty(verbVal))
          continue;
        var opId = GetRdfsLabel(g, cNode);
        if (string.IsNullOrEmpty(opId))
          opId = Guid.NewGuid().ToString();

        var operation = new OpenApiOperation
        {
          OperationId = opId,
          Parameters = new List<OpenApiParameter>(),
          Responses = new OpenApiResponses()
        };

        operation.Summary = GetLiteral(g, cNode, "operationSummary");
        operation.Description = GetLiteral(g, cNode, "operationDescription");

        ReconstructParameters(g, cNode, operation);

        var rbDesc = GetLiteral(g, cNode, "requestBodyDescription");
        var rbRef = GetLiteral(g, cNode, "requestBodyRef");
        var inlineReq = GetInlineSchemaNode(g, cNode, "hasInlineSchema_inlineRequestBody");
        if (!string.IsNullOrEmpty(rbDesc) || !string.IsNullOrEmpty(rbRef) || (inlineReq != null))
        {
          var reqBody = new OpenApiRequestBody
          {
            Description = rbDesc,
            Content = new Dictionary<string, OpenApiMediaType>()
          };
          if (!string.IsNullOrEmpty(rbRef))
          {
            reqBody.Content.Add("application/json", new OpenApiMediaType
            {
              Schema = new OpenApiSchema
              {
                Reference = new OpenApiReference { Id = rbRef, Type = ReferenceType.Schema }
              }
            });
          }
          else if (inlineReq != null)
          {
            var inlSchema = ReconstructInlineSchema(g, inlineReq);
            reqBody.Content.Add("application/json", new OpenApiMediaType { Schema = inlSchema });
          }
          operation.RequestBody = reqBody;
        }

        ReconstructResponses(g, cNode, operation);

        if (!doc.Paths.ContainsKey(pathVal))
          doc.Paths[pathVal] = new OpenApiPathItem();
        var pathItem = doc.Paths[pathVal];
        if (!Enum.TryParse<OperationType>(verbVal, true, out var ot))
          ot = OperationType.Get;
        pathItem.AddOperation(ot, operation);
      }
    }

    private void ReconstructParameters(IGraph g, INode opNode, OpenApiOperation operation)
    {
      var hasParameter = g.CreateUriNode(UriFor("hasParameter"));
      var triples = g.GetTriplesWithSubjectPredicate(opNode, hasParameter).ToList();
      foreach (var t in triples)
      {
        var paramNode = t.Object;
        if (paramNode == null) continue;
        var pName = GetLiteral(g, paramNode, "paramName");
        var pLoc = GetLiteral(g, paramNode, "paramLocation");
        var pReq = GetLiteral(g, paramNode, "paramRequired");
        var pDesc = GetLiteral(g, paramNode, "paramDescription");
        var pType = GetLiteral(g, paramNode, "paramType");
        var pFmt = GetLiteral(g, paramNode, "paramFormat");
        var pEnum = GetLiteral(g, paramNode, "paramEnum");
        if (string.IsNullOrEmpty(pName)) continue;

        var param = new OpenApiParameter
        {
          Name = pName,
          Description = pDesc,
          Required = (pReq == "True" || pReq == "true"),
          In = ParameterLocation.Query,
          Schema = new OpenApiSchema { Type = string.IsNullOrEmpty(pType) ? "string" : pType, Format = pFmt }
        };
        if (!string.IsNullOrEmpty(pEnum))
        {
          var enumVals = pEnum.Split(';').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
          param.Schema.Enum = enumVals.Select(ev => (IOpenApiAny)new OpenApiString(ev)).ToList();
        }
        if (!string.IsNullOrEmpty(pLoc) && Enum.TryParse<ParameterLocation>(pLoc, true, out var locParsed))
          param.In = locParsed;

        operation.Parameters.Add(param);
      }
    }

    private void ReconstructResponses(IGraph g, INode opNode, OpenApiOperation operation)
    {
      var tripleSet = g.GetTriplesWithSubject(opNode).ToList();
      foreach (var tr in tripleSet)
      {
        if (tr.Predicate is IUriNode uriP)
        {
          var uriStr = uriP.Uri.ToString();
          if (!uriStr.StartsWith(_baseNs)) continue;
          var localName = uriStr.Substring(_baseNs.Length);
          if (localName.StartsWith("responseDescription_"))
          {
            var code = localName.Replace("responseDescription_", "");
            var desc = tr.Object.AsValuedNode().AsString();
            if (!operation.Responses.ContainsKey(code))
              operation.Responses[code] = new OpenApiResponse();
            operation.Responses[code].Description = desc;
          }
          else if (localName.StartsWith("responseSchemaRef_"))
          {
            var code = localName.Replace("responseSchemaRef_", "");
            var refVal = tr.Object.AsValuedNode().AsString();
            if (!operation.Responses.ContainsKey(code))
              operation.Responses[code] = new OpenApiResponse();
            var schema = new OpenApiSchema
            {
              Reference = new OpenApiReference { Id = refVal, Type = ReferenceType.Schema }
            };
            operation.Responses[code].Content = new Dictionary<string, OpenApiMediaType>
                        {
                            { "application/json", new OpenApiMediaType { Schema = schema } }
                        };
          }
        }
      }
      foreach (var code in PossibleHttpCodes())
      {
        var predNode = g.CreateUriNode(UriFor("hasInlineSchema_inlineResponse_" + code));
        var foundTriples = g.GetTriplesWithSubjectPredicate(opNode, predNode).ToList();
        if (foundTriples.Count == 0) continue;
        if (!operation.Responses.ContainsKey(code))
          operation.Responses[code] = new OpenApiResponse();
        if (operation.Responses[code].Content == null)
          operation.Responses[code].Content = new Dictionary<string, OpenApiMediaType>();
        var schemaNode = foundTriples.First().Object;
        if (schemaNode != null)
        {
          var inlSchema = ReconstructInlineSchema(g, schemaNode);
          operation.Responses[code].Content["application/json"] = new OpenApiMediaType { Schema = inlSchema };
        }
      }
    }

    private IEnumerable<string> PossibleHttpCodes()
    {
      return new List<string> { "200", "201", "202", "204", "400", "401", "403", "404", "500", "501", "207", "default" };
    }
    #endregion

    #region COMPONENTS.SCHEMAS
    private void ReconstructSchemas(IGraph g, OpenApiDocument doc)
    {
      var owlClass = g.CreateUriNode("owl:Class");
      var allClasses = g.GetTriplesWithPredicateObject(g.CreateUriNode("rdf:type"), owlClass)
                        .Select(t => t.Subject)
                        .Distinct()
                        .ToList();
      foreach (var cNode in allClasses)
      {
        // Se il nodo ha "originalPath", è un'operazione, saltalo
        var opPathVal = GetLiteral(g, cNode, "originalPath");
        if (!string.IsNullOrEmpty(opPathVal))
          continue;
        var schemaName = GetRdfsLabel(g, cNode);
        if (string.IsNullOrEmpty(schemaName))
          continue;
        var schema = new OpenApiSchema();
        schema.Title = GetLiteral(g, cNode, "schemaTitle");
        schema.Description = GetLiteral(g, cNode, "schemaDescription");
        schema.Type = GetLiteral(g, cNode, "schemaType");
        var fmt = GetLiteral(g, cNode, "schemaFormat");
        if (!string.IsNullOrEmpty(fmt))
          schema.Format = fmt;
        var enumCsv = GetLiteral(g, cNode, "schemaEnum");
        if (!string.IsNullOrEmpty(enumCsv))
        {
          var evs = enumCsv.Split(';').Select(x => x.Trim()).ToList();
          schema.Enum = evs.Select(x => (IOpenApiAny)new OpenApiString(x)).ToList();
        }
        if (schema.Type == "array")
        {
          var iref = GetLiteral(g, cNode, "itemsRef");
          if (!string.IsNullOrEmpty(iref))
          {
            schema.Items = new OpenApiSchema
            {
              Reference = new OpenApiReference { Id = iref, Type = ReferenceType.Schema }
            };
          }
          else
          {
            var arrInlNode = GetInlineSchemaNode(g, cNode, "hasInlineSchema_arrayItems");
            if (arrInlNode != null)
              schema.Items = ReconstructInlineSchema(g, arrInlNode);
          }
        }
        var reqFields = GetLiteral(g, cNode, "schemaRequiredFields");
        if (!string.IsNullOrEmpty(reqFields))
          schema.Required = reqFields.Split(',').Select(s => s.Trim()).ToHashSet();

        // Proprietà
        schema.Properties = new Dictionary<string, OpenApiSchema>();
        var hasProp = g.CreateUriNode(UriFor("hasSchemaProperty"));
        var propTriples = g.GetTriplesWithSubjectPredicate(cNode, hasProp).ToList();
        foreach (var pt in propTriples)
        {
          var pNode = pt.Object;
          if (pNode == null) continue;
          var propName = GetLiteral(g, pNode, "propertyName");
          if (string.IsNullOrEmpty(propName)) continue;
          var subSchema = new OpenApiSchema();
          subSchema.Type = GetLiteral(g, pNode, "propertyType");
          var subFormat = GetLiteral(g, pNode, "propertyFormat");
          if (!string.IsNullOrEmpty(subFormat))
            subSchema.Format = subFormat;
          var pref = GetLiteral(g, pNode, "propertyRef");
          if (!string.IsNullOrEmpty(pref))
          {
            subSchema.Reference = new OpenApiReference { Id = pref, Type = ReferenceType.Schema };
          }
          else
          {
            var inlinePropNode = GetInlineSchemaNode(g, pNode, "hasInlineSchema_inlineProp");
            if (inlinePropNode != null)
              subSchema = ReconstructInlineSchema(g, inlinePropNode);
          }
          var pDesc = GetLiteral(g, pNode, "propertyDescription");
          if (!string.IsNullOrEmpty(pDesc))
            subSchema.Description = pDesc;
          var pEnumCsv = GetLiteral(g, pNode, "propertyEnum");
          if (!string.IsNullOrEmpty(pEnumCsv))
          {
            var vls = pEnumCsv.Split(';').Select(x => x.Trim()).ToList();
            subSchema.Enum = vls.Select(x => (IOpenApiAny)new OpenApiString(x)).ToList();
          }
          schema.Properties[propName] = subSchema;
        }

        schema.OneOf = new List<OpenApiSchema>();
        int idx = 0;
        while (true)
        {
          var oneOfRefVal = GetLiteral(g, cNode, $"oneOfRef_{idx}");
          var inlNode = GetInlineSchemaNode(g, cNode, $"hasInlineSchema_oneOfInline_{idx}");
          if (string.IsNullOrEmpty(oneOfRefVal) && inlNode == null)
            break;
          var subSchema = new OpenApiSchema();
          if (!string.IsNullOrEmpty(oneOfRefVal))
          {
            subSchema.Reference = new OpenApiReference { Id = oneOfRefVal, Type = ReferenceType.Schema };
            schema.OneOf.Add(subSchema);
          }
          else if (inlNode != null)
          {
            subSchema = ReconstructInlineSchema(g, inlNode);
            schema.OneOf.Add(subSchema);
          }
          idx++;
        }
        schema.AllOf = new List<OpenApiSchema>();
        idx = 0;
        while (true)
        {
          var allOfRefVal = GetLiteral(g, cNode, $"allOfRef_{idx}");
          var inlNode = GetInlineSchemaNode(g, cNode, $"hasInlineSchema_allOfInline_{idx}");
          if (string.IsNullOrEmpty(allOfRefVal) && inlNode == null)
            break;
          var subSchema = new OpenApiSchema();
          if (!string.IsNullOrEmpty(allOfRefVal))
          {
            subSchema.Reference = new OpenApiReference { Id = allOfRefVal, Type = ReferenceType.Schema };
            schema.AllOf.Add(subSchema);
          }
          else if (inlNode != null)
          {
            subSchema = ReconstructInlineSchema(g, inlNode);
            schema.AllOf.Add(subSchema);
          }
          idx++;
        }

        if (!doc.Components.Schemas.ContainsKey(schemaName))
          doc.Components.Schemas[schemaName] = schema;
      }
    }
    #endregion

    #region GESTIONE INLINE SCHEMA
    /// <summary>
    /// Ricostruisce uno schema inline (definito in un blank node di tipo :InlineSchema)
    /// e restituisce l'oggetto OpenApiSchema.
    /// </summary>
    private OpenApiSchema ReconstructInlineSchema(IGraph g, INode schemaNode)
    {
      var s = new OpenApiSchema();
      s.Type = GetLiteral(g, schemaNode, "inlineSchemaType");
      s.Format = GetLiteral(g, schemaNode, "inlineSchemaFormat");

      var eCsv = GetLiteral(g, schemaNode, "inlineSchemaEnum");
      if (!string.IsNullOrEmpty(eCsv))
      {
        var vals = eCsv.Split(';').Select(x => x.Trim()).ToList();
        s.Enum = vals.Select(v => (IOpenApiAny)new OpenApiString(v)).ToList();
      }

      var reqs = GetLiteral(g, schemaNode, "inlineRequired");
      if (!string.IsNullOrEmpty(reqs))
      {
        s.Required = reqs.Split(',').Select(x => x.Trim()).ToHashSet();
      }

      if (s.Type == "array")
      {
        var arrRef = GetLiteral(g, schemaNode, "inlineArrayItemRef");
        if (!string.IsNullOrEmpty(arrRef))
        {
          s.Items = new OpenApiSchema
          {
            Reference = new OpenApiReference { Id = arrRef, Type = ReferenceType.Schema }
          };
        }
        else
        {
          var arrInline = GetInlineSchemaNode(g, schemaNode, "hasInlineSchema_nestedItems");
          if (arrInline != null)
            s.Items = ReconstructInlineSchema(g, arrInline);
        }
      }

      if (s.Type == "object")
      {
        s.Properties = new Dictionary<string, OpenApiSchema>();
        var hasProp = g.CreateUriNode(UriFor("hasInlineProperty"));
        var pTrips = g.GetTriplesWithSubjectPredicate(schemaNode, hasProp).ToList();
        foreach (var pt in pTrips)
        {
          var propNode = pt.Object;
          if (propNode == null) continue;
          var pn = GetLiteral(g, propNode, "inlinePropertyName");
          if (string.IsNullOrEmpty(pn)) continue;
          var subS = new OpenApiSchema();
          var pType = GetLiteral(g, propNode, "inlinePropertyType");
          var pFormat = GetLiteral(g, propNode, "inlinePropertyFormat");
          subS.Type = pType;
          if (!string.IsNullOrEmpty(pFormat))
            subS.Format = pFormat;
          var pr = GetLiteral(g, propNode, "inlinePropertyRef");
          if (!string.IsNullOrEmpty(pr))
            subS.Reference = new OpenApiReference { Id = pr, Type = ReferenceType.Schema };
          else
          {
            var nestedNode = GetInlineSchemaNode(g, propNode, "hasInlineSchema_nestedPropertySchema");
            if (nestedNode != null)
              subS = ReconstructInlineSchema(g, nestedNode);
          }
          var pDesc = GetLiteral(g, propNode, "inlinePropertyDescription");
          if (!string.IsNullOrEmpty(pDesc))
            subS.Description = pDesc;
          s.Properties[pn] = subS;
        }
      }

      s.OneOf = new List<OpenApiSchema>();
      int idx = 0;
      while (true)
      {
        var refVal = GetLiteral(g, schemaNode, $"oneOfRef_{idx}");
        var inlNode = GetInlineSchemaNode(g, schemaNode, $"hasInlineSchema_oneOf_inline_{idx}");
        if (string.IsNullOrEmpty(refVal) && inlNode == null)
          break;
        var sub = new OpenApiSchema();
        if (!string.IsNullOrEmpty(refVal))
        {
          sub.Reference = new OpenApiReference { Id = refVal, Type = ReferenceType.Schema };
          s.OneOf.Add(sub);
        }
        else if (inlNode != null)
        {
          sub = ReconstructInlineSchema(g, inlNode);
          s.OneOf.Add(sub);
        }
        idx++;
      }

      s.AllOf = new List<OpenApiSchema>();
      idx = 0;
      while (true)
      {
        var refVal = GetLiteral(g, schemaNode, $"allOfRef_{idx}");
        var inlNode = GetInlineSchemaNode(g, schemaNode, $"hasInlineSchema_allOf_inline_{idx}");
        if (string.IsNullOrEmpty(refVal) && inlNode == null)
          break;
        var sub = new OpenApiSchema();
        if (!string.IsNullOrEmpty(refVal))
        {
          sub.Reference = new OpenApiReference { Id = refVal, Type = ReferenceType.Schema };
          s.AllOf.Add(sub);
        }
        else if (inlNode != null)
        {
          sub = ReconstructInlineSchema(g, inlNode);
          s.AllOf.Add(sub);
        }
        idx++;
      }
      return s;
    }

    private INode GetInlineSchemaNode(IGraph g, INode parentNode, string propertyName)
    {
      var pNode = g.CreateUriNode(UriFor(propertyName));
      var t = g.GetTriplesWithSubjectPredicate(parentNode, pNode).FirstOrDefault();
      return t?.Object;
    }
    #endregion

    #region FUNZIONI UTILI
    private string GetLiteral(IGraph g, INode subject, string localName)
    {
      var pred = g.CreateUriNode(UriFor(localName));
      var triple = g.GetTriplesWithSubjectPredicate(subject, pred).FirstOrDefault();
      if (triple != null && triple.Object is ILiteralNode ln)
        return ln.Value;
      return null;
    }

    private string GetRdfsLabel(IGraph g, INode subject)
    {
      var triple = g.GetTriplesWithSubjectPredicate(subject, g.CreateUriNode("rdfs:label")).FirstOrDefault();
      if (triple != null && triple.Object is ILiteralNode ln)
        return ln.Value;
      return null;
    }

    private Uri UriFor(string localName)
    {
      return new Uri(_baseNs + localName);
    }
    #endregion
  }
}
