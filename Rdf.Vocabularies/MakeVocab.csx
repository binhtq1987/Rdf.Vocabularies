﻿#r "Microsoft.CSharp"
#r "System.Core"
#r "System.Xml"
#r "..\packages\dotNetRDF\lib\net40\dotNetRDF.dll"
#r "..\packages\VDS.Common\lib\net40-client\VDS.Common.dll"

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices.ComTypes;
using System.Text.RegularExpressions;
using System.Xml;
using System.IO;
using VDS.RDF;
using VDS.RDF.Ontology;
using VDS.RDF.Parsing;
using VDS.RDF.Query;
using VDS.RDF.Query.Builder;
using VDS.RDF.Query.Inference;

private IList<string> CSharpKeywords = new[]
{
    "event",
    "object",
    "abstract",
};

private Regex MemberRegex = new Regex("[^A-Za-z0-9]");

private static class DcTerms
{
    public const string title = "http://purl.org/dc/terms/title";
    public const string description = "http://purl.org/dc/terms/description";
}

private static class Dc
{
    public const string title = "http://purl.org/dc/elements/1.1/title";
    public const string description = "http://purl.org/dc/elements/1.1/description";
}

private static class Rdfs
{
    public const string label = "http://www.w3.org/2000/01/rdf-schema#label";
    public const string comment = "http://www.w3.org/2000/01/rdf-schema#comment";
    public const string subClassOf = "http://www.w3.org/2000/01/rdf-schema#subClassOf";
    public const string isDefinedBy = "http://www.w3.org/2000/01/rdf-schema#isDefinedBy";
}

private static class Rdf
{
    public const string type = "http://www.w3.org/1999/02/22-rdf-syntax-ns#type";
    public const string Property = "http://www.w3.org/1999/02/22-rdf-syntax-ns#Property";
}

private static class Skos
{
    public const string scopeNote = "http://www.w3.org/2004/02/skos/core#scopeNote";
    public const string example = "http://www.w3.org/2004/02/skos/core#example";
}

public void CreateVocabulary(dynamic Output, string file, string @namespace = "Vocab", string ontologyId = null, bool skipDefinedByCheck = false)
{
    var path = Path.Combine(Directory.GetCurrentDirectory(), file);
    var writer = Output[Path.Combine(Directory.GetCurrentDirectory(), "obj", file) + ".g.cs"];

    writer.WriteLine($@"// <auto-generated />
// ReSharper disable InconsistentNaming
// ReSharper disable CheckNamespace

namespace {@namespace}
{{");

    Uri ontologyUri = null;
    if (ontologyId != null)
    {
        ontologyUri = new Uri(ontologyId);
    }

    WriteClass(writer, path, ontologyUri, skipDefinedByCheck);

    writer.WriteLine("}");
}

private void ApplyRulesAndReasoning(OntologyGraph graph)
{
    StaticRdfsReasoner reasoner = new StaticRdfsReasoner();
    reasoner.Initialise(graph);
    reasoner.Apply(graph);

    var q = QueryBuilder.Construct(g => g.Where(t => t.Subject("prop").PredicateUri(new Uri(Rdf.type)).Object(new Uri(Rdf.Property))))
                        .Graph("g", g => 
                            g.Where(t => 
                                t.Subject("prop").PredicateUri(new Uri(Rdf.type)).Object("type")
                                 .Subject("type").PredicateUri(new Uri(Rdfs.subClassOf)).Object(new Uri(Rdf.Property))))
                        .BuildQuery();

    var store = new TripleStore();
    store.Add(graph);

    dynamic constructed = new VDS.RDF.Query.LeviathanQueryProcessor(store).ProcessQuery(q);
    graph.Merge(constructed);
}

private void WriteClass(dynamic Output, string ontologyPath, Uri ontologyId = null, bool skipDefinedByCheck = false)
{
    var prefix = Path.GetFileNameWithoutExtension(ontologyPath).ToLower();
    var className = System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(prefix);
    OntologyGraph g = new OntologyGraph();
    FileLoader.Load(g, ontologyPath);

    ApplyRulesAndReasoning(g);

    ontologyId = ontologyId ?? g.BaseUri;

    var ontology = new Ontology(g.CreateUriNode(ontologyId), g);
    
    var description = Get(g, ontology.Resource, DcTerms.title, Rdfs.label, Dc.title);
    var remarks = Get(g, ontology.Resource, DcTerms.description, Rdfs.comment, Dc.description);

    Output.WriteLine($@"    /// <summary>{description} ({ontologyId}).</summary>");

    if (remarks != null)
    {
        Output.WriteLine($@"    /// <remarks>{remarks}</remarks>");
    }

    Output.WriteLine($@"    public static partial class {className}
    {{
        /// <summary>
        /// Vocabulary prefix
        /// </summary>
        /// <value>{prefix}</value>
        public const string Prefix = ""{prefix}"";

        /// <summary>
        /// Vocabulary base URI
        /// </summary>
        /// <value>{ontologyId}</value>
        public const string BaseUri = ""{ontologyId}"";");

    WriteOntologyClasses(Output, g, ontology.Resource as IUriNode, skipDefinedByCheck);
    WriteOntologyProperties(Output, g, ontology.Resource as IUriNode, skipDefinedByCheck);
    WriteEverythingElse(Output, g, ontology.Resource as IUriNode);

    Output.WriteLine("    }");
}

private void WriteOntologyClasses(dynamic Output, OntologyGraph ontology, IUriNode ontologyResource, bool skipDefinedByCheck)
{
    foreach (OntologyClass clas in DefinedTerms(ontology.AllClasses, ontologyResource, skipDefinedByCheck))
    {
        var resourceNode = clas.Resource as IUriNode;

        // skip union classes, etc.
        if (resourceNode == null) continue;

        Console.WriteLine("Writing class {0}", clas.Resource);
        WriteTerm(Output, ontology, clas.Resource as IUriNode, ontologyResource, " class");
    }
}

private void WriteOntologyProperties(dynamic Output, OntologyGraph ontology, IUriNode ontologyResource, bool skipDefinedByCheck)
{
    foreach (OntologyProperty prop in DefinedTerms(ontology.AllProperties, ontologyResource, skipDefinedByCheck))
    {
        Console.WriteLine("Writing property {0}", prop.Resource);
        WriteTerm(Output, ontology, prop.Resource as IUriNode, ontologyResource, " property");
    }
}

private void WriteEverythingElse(dynamic Output, OntologyGraph ontology, IUriNode ontologyResource)
{
    var knownTerms = ontology.AllClasses.Select(c => c.Resource)
        .Union(ontology.AllProperties.Select(p => p.Resource)).ToList();

    foreach (var triple in ontology.GetTriplesWithPredicate(ontology.CreateUriNode(new Uri(Rdfs.isDefinedBy))))
    {
        if (knownTerms.Contains(triple.Subject)) continue;

        Console.WriteLine("Writing {0}", triple.Subject);
        WriteTerm(Output, ontology, triple.Subject as IUriNode, ontologyResource);
    }
}

private void WriteTerm(dynamic Output, OntologyGraph g, IUriNode resourceNode, IUriNode ontologyResource, string suffix = "")
{
    var description = Get(g, resourceNode, Rdfs.label, DcTerms.title);
    var name = ontologyResource.Uri.MakeRelativeUri(resourceNode.Uri).ToString().Trim('#');
    var id = resourceNode.Uri;
    var remarks = Get(g, resourceNode, Skos.scopeNote, Rdfs.comment);
    var example = Get(g, resourceNode, Skos.example);

    if (!ontologyResource.Uri.IsBaseOf(id))
    {
        Console.WriteLine("Skipped {0}", id);
        return;
    }

    if (name.Length == 0)
    {
        Console.WriteLine("Skipped '{0}' because it's not a valid C# member name.", name);
        return;
    }
    
    WriteMember(Output, description + suffix, remarks, example, MemberRegex.Replace(name, "_"), id);
}

private static IEnumerable<T> DefinedTerms<T>(IEnumerable<T> terms, IUriNode ontology, bool skipDefinedByCheck)
where T : OntologyResource
{
    return (from term in terms
            where skipDefinedByCheck || term.IsDefinedBy.Any(definedBy => definedBy.ToString() == ontology.ToString())
            select term).Distinct(new Comparer<T>());
}

private void WriteMember(dynamic Output, object description, object remarks, object example, string name, object id)
{
    name = EnsureEscaped(name);

    Output.WriteLine();
    Output.WriteLine($"        /// <summary>{description}</summary>");
    Output.WriteLine($"        /// <value>{id}</value>");

    if (remarks != null)
    {
        Output.WriteLine($"        /// <remarks>{remarks}</remarks>");
    }
    if (example != null)
    {
        Output.WriteLine($"        /// <example>{example}</example>");
    }

    Output.WriteLine($@"        public const string {name} = ""{id}"";");
}

private string Get(OntologyGraph g, INode resource, params string[] predicates)
{
    foreach (var predicate in predicates)
    {
        var node = (from triple in g.GetTriplesWithSubject(resource)
                    let triplePredicate = (triple.Predicate as IUriNode)?.Uri
                    where predicate != null
                    where Uri.Compare(triplePredicate, new Uri(predicate), UriComponents.AbsoluteUri, UriFormat.SafeUnescaped, StringComparison.Ordinal) == 0
                    select triple).FirstOrDefault()?.Object as ILiteralNode;

        if (node != null)
        {
            return System.Web.HttpUtility.HtmlEncode(Regex.Replace(node.Value, @"\s+", " ", RegexOptions.Multiline).Trim());
        }
    }

    return null;
}

private string EnsureEscaped(string name)
{
    if (CSharpKeywords.Contains(name))
    {
        return $"@{name}";
    }

    return name;
}

private class Comparer<T> : IEqualityComparer<T> where T : OntologyResource
{
    public bool Equals(T x, T y)
    {
        if (x.Resource is IUriNode && y.Resource is IUriNode)
        {
            return Uri.Compare((x.Resource as IUriNode).Uri, (y.Resource as IUriNode).Uri, UriComponents.AbsoluteUri, UriFormat.SafeUnescaped, StringComparison.Ordinal) == 0;
        }

        return x.Resource == y.Resource;
    }

    public int GetHashCode(T obj)
    {
        return obj.Resource.GetHashCode();
    }
}