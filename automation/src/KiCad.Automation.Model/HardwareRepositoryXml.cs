using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;

namespace KiCad.Automation.Model;

/// <summary>Repository composition only; does not open files or infer electrical compatibility.</summary>
public static class HardwareRepositoryXml
{
    public const string Namespace = "urn:kicad:automation:hardware:1";
    private static readonly XNamespace Ns = Namespace;
    private static readonly Lazy<XmlSchemaSet> Schema = new(() =>
    {
        var result = new XmlSchemaSet { XmlResolver = null };
        var assembly = typeof(HardwareRepositoryXml).Assembly;
        string name = assembly.GetManifestResourceNames().Single(n => n.EndsWith("hardware-v1.xsd", StringComparison.Ordinal));
        using Stream stream = assembly.GetManifestResourceStream(name)!;
        using XmlReader reader = XmlReader.Create(stream, Settings());
        result.Add(Namespace, reader);
        result.Compile();
        return result;
    });

    public static HardwareRepository Read(string xml)
    {
        try
        {
            using var input = new StringReader(xml);
            using XmlReader reader = XmlReader.Create(input, Settings());
            XDocument document = XDocument.Load(reader, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
            if (document.Root?.Name != Ns + "hardware")
                throw HardwareRepository.Invalid("Expected a supported hardware namespace and root element.");
            document.Validate(Schema.Value, null);
            XElement root = document.Root;
            IEnumerable<XElement> Rows(string group, string item) => root.Element(Ns + group)!.Elements(Ns + item);
            var repository = new HardwareRepository(Id(root), Text(root, "name"),
                Rows("designs", "design").Select(d => new HardwareDesign(Id(d), Text(d, "name"), Text(d, "project"), Text(d, "model"),
                    d.Elements(Ns + "port").Select(p => new HardwarePort(Id(p), Text(p, "name"), p.Element(Ns + "description")!.Value)).ToArray())).ToArray(),
                Rows("documents", "document").Select(d => new HardwareDocument(Id(d), Text(d, "path"), Text(d, "revision"), d.Element(Ns + "purpose")!.Value)).ToArray(),
                Rows("libraries", "library").Select(l => new HardwareLibrary(Id(l), Text(l, "path"), Text(l, "revision"))).ToArray(),
                Rows("interfaces", "interface").Select(i => new HardwareInterface(Id(i), Text(i, "name"), i.Element(Ns + "description")!.Value,
                    i.Elements(Ns + "endpoint").Select(e => new HardwareEndpoint(Id(e, "design"), Id(e, "port"))).ToArray())).ToArray());
            repository.Validate();
            return repository;
        }
        catch (Exception error) when (error is XmlException or XmlSchemaException or FormatException or OverflowException)
        {
            throw HardwareRepository.Invalid("Invalid hardware XML: " + error.Message);
        }
    }

    public static string Write(HardwareRepository repository)
    {
        repository.Validate();
        return E("hardware", A("id", repository.Id), A("name", repository.Name), A("version", 1),
            E("designs", repository.Designs.OrderBy(d => d.Id).Select(d => E("design", A("id", d.Id), A("name", d.Name), A("project", d.ProjectPath), A("model", d.ModelPath),
                d.Ports.OrderBy(p => p.Id).Select(p => E("port", A("id", p.Id), A("name", p.Name), E("description", p.Description)))))),
            E("documents", repository.Documents.OrderBy(d => d.Id).Select(d => E("document", A("id", d.Id), A("path", d.Path), A("revision", d.Revision), E("purpose", d.Purpose)))),
            E("libraries", repository.Libraries.OrderBy(l => l.Id).Select(l => E("library", A("id", l.Id), A("path", l.Path), A("revision", l.Revision)))),
            E("interfaces", repository.Interfaces.OrderBy(i => i.Id).Select(i => E("interface", A("id", i.Id), A("name", i.Name), E("description", i.Description),
                i.Endpoints.OrderBy(e => e.DesignId).ThenBy(e => e.PortId).Select(e => E("endpoint", A("design", e.DesignId), A("port", e.PortId)))))))
            .ToString(SaveOptions.None) + "\n";
    }

    private static XmlReaderSettings Settings() => new()
    {
        DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 16 * 1024 * 1024
    };
    private static XElement E(string name, params object?[] content) => new(Ns + name, content);
    private static XAttribute A(string name, object value) => new(name, value);
    private static string Text(XElement element, string name) => element.Attribute(name)!.Value;
    private static Guid Id(XElement element, string name = "id") => Guid.ParseExact(Text(element, name), "D");
}
