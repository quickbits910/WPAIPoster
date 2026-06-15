using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Linq;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Metadata.Profiles.Iptc;
using SixLabors.ImageSharp.Metadata.Profiles.Xmp;
using SixLabors.ImageSharp.PixelFormats;
using WPAIPoster.Images;

namespace WPAIPoster.Tests;

public class ImageTagReaderTests : IDisposable
{
    private readonly string _dir;
    private readonly ImageTagReader _reader = new();

    public ImageTagReaderTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"WPAITagRead_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static readonly XNamespace Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    private static readonly XNamespace X = "adobe:ns:meta/";

    private static XmpProfile XmpWithSubjects(params string[] subjects)
    {
        var bag = new XElement(Rdf + "Bag", subjects.Select(s => new XElement(Rdf + "li", s)));
        var doc = new XDocument(
            new XElement(X + "xmpmeta",
                new XElement(Rdf + "RDF",
                    new XElement(Rdf + "Description",
                        new XElement(Dc + "subject", bag)))));
        using var ms = new MemoryStream();
        doc.Save(ms);
        return new XmpProfile(ms.ToArray());
    }

    private string SaveJpeg(Action<Image<Rgba32>> configure)
    {
        string path = Path.Combine(_dir, $"{Guid.NewGuid():N}.jpg");
        using var img = new Image<Rgba32>(16, 16);
        configure(img);
        img.SaveAsJpeg(path);
        return path;
    }

    [Fact]
    public void ReadTags_ReadsXmpSubjects()
    {
        string path = SaveJpeg(img => img.Metadata.XmpProfile = XmpWithSubjects("AI.Mountain", "AI.Lake"));

        var tags = _reader.ReadTags(path);

        Assert.Contains("AI.Mountain", tags);
        Assert.Contains("AI.Lake", tags);
    }

    [Fact]
    public void ReadTags_ReadsIptcKeywords()
    {
        string path = SaveJpeg(img =>
        {
            var iptc = new IptcProfile();
            iptc.SetValue(IptcTag.Keywords, "landscape");
            iptc.SetValue(IptcTag.Keywords, "sunset");
            img.Metadata.IptcProfile = iptc;
        });

        var tags = _reader.ReadTags(path);

        Assert.Contains("landscape", tags);
    }

    [Fact]
    public void ReadTags_DedupesAcrossBackends()
    {
        string path = SaveJpeg(img =>
        {
            img.Metadata.XmpProfile = XmpWithSubjects("mountain");
            var iptc = new IptcProfile();
            iptc.SetValue(IptcTag.Keywords, "mountain");
            img.Metadata.IptcProfile = iptc;
        });

        var tags = _reader.ReadTags(path);

        Assert.Equal(1, tags.Count(t => string.Equals(t, "mountain", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void ReadTags_NoMetadata_ReturnsEmpty()
    {
        string path = SaveJpeg(_ => { });
        Assert.Empty(_reader.ReadTags(path));
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int setxattr(string path, string name, byte[] value, nint size, int flags);

    [Fact]
    public void ReadTags_ReadsLinuxXattr()
    {
        if (!OperatingSystem.IsLinux())
            return; // xattr is Linux-only

        string path = SaveJpeg(_ => { });
        byte[] value = Encoding.UTF8.GetBytes("AI.Beach,AI.Ocean");
        int rc = setxattr(path, "user.xdg.tags", value, value.Length, 0);
        if (rc != 0)
            return; // filesystem may not support user xattrs (e.g. some tmpfs configs)

        var tags = _reader.ReadTags(path);

        Assert.Contains("AI.Beach", tags);
        Assert.Contains("AI.Ocean", tags);
    }
}
