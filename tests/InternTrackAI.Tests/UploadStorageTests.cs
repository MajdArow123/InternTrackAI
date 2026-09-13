using InternTrackAI.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;

namespace InternTrackAI.Tests;

public class UploadStorageTests : IDisposable
{
    private readonly string _contentRoot;
    private readonly UploadStorage _storage;

    public UploadStorageTests()
    {
        _contentRoot = Path.Combine(Path.GetTempPath(), "interntrack-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_contentRoot);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        _storage = new UploadStorage(config, new FakeEnv(_contentRoot));
    }

    public void Dispose()
    {
        try { Directory.Delete(_contentRoot, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Root_defaults_to_uploads_under_content_root_and_is_created()
    {
        Assert.Equal(Path.Combine(_contentRoot, "uploads"), _storage.Root);
        Assert.True(Directory.Exists(_storage.Root));
        Assert.True(Directory.Exists(_storage.PhotosDirectory));
    }

    [Fact]
    public void Root_honours_UPLOADS_PATH_when_configured()
    {
        var custom = Path.Combine(_contentRoot, "vol", "data");
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [UploadStorage.ConfigKey] = custom })
            .Build();
        var storage = new UploadStorage(config, new FakeEnv(_contentRoot));
        Assert.Equal(Path.GetFullPath(custom), storage.Root);
    }

    [Theory]
    [InlineData("resumes/user1/file.pdf")]
    [InlineData("uploads/resumes/user1/file.pdf")]   // legacy prefix written before UPLOADS_PATH existed
    [InlineData("Uploads/resumes/user1/file.pdf")]   // legacy prefix, different casing
    [InlineData("resumes\\user1\\file.pdf")]         // Windows separators
    public void Resolve_maps_relative_and_legacy_paths_under_the_root(string stored)
    {
        var expected = Path.Combine(_storage.Root, "resumes", "user1", "file.pdf");
        Assert.Equal(expected, _storage.Resolve(stored));
    }

    [Fact]
    public void Resolve_keeps_a_directory_that_merely_starts_with_uploads()
    {
        // "uploads2/..." is not the legacy prefix and must not be stripped.
        var full = _storage.Resolve("uploads2/x.pdf");
        Assert.Equal(Path.Combine(_storage.Root, "uploads2", "x.pdf"), full);
    }

    [Theory]
    [InlineData("../secret.txt")]
    [InlineData("resumes/../../etc/passwd")]
    [InlineData("uploads/../../etc/passwd")]
    [InlineData("resumes/user1/../../../outside.pdf")]
    [InlineData("..\\..\\windows\\system.ini")]
    public void Resolve_rejects_parent_directory_traversal(string stored)
    {
        Assert.Throws<InvalidOperationException>(() => _storage.Resolve(stored));
    }

    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData("/uploads/resumes/user1/file.pdf")]
    [InlineData("C:/Windows/system.ini")]
    [InlineData("C:\\Windows\\system.ini")]
    public void Resolve_rejects_rooted_paths(string stored)
    {
        Assert.Throws<InvalidOperationException>(() => _storage.Resolve(stored));
    }

    [Fact]
    public void Resolve_rejects_sibling_directory_trick()
    {
        // Root is ".../uploads"; ".../uploads-evil" shares the prefix but is a different directory.
        Assert.Throws<InvalidOperationException>(() => _storage.Resolve("../uploads-evil/file.pdf"));
        Assert.Throws<InvalidOperationException>(() => _storage.Resolve("../uploads2/file.pdf"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_rejects_empty_paths(string stored)
    {
        Assert.Throws<InvalidOperationException>(() => _storage.Resolve(stored));
    }

    [Fact]
    public void Resolve_never_returns_the_root_itself()
    {
        // "." would resolve to the root directory, which is not a document.
        Assert.Throws<InvalidOperationException>(() => _storage.Resolve("."));
        Assert.Throws<InvalidOperationException>(() => _storage.Resolve("resumes/.."));
    }

    [Fact]
    public void MakeStoredPath_uses_forward_slashes_relative_to_root()
    {
        Assert.Equal("resumes/u1/abc.pdf", UploadStorage.MakeStoredPath("resumes", "u1", "abc.pdf"));
    }

    [Fact]
    public void Delete_and_Exists_round_trip()
    {
        var dir = _storage.GetUserDirectory("resumes", "u1");
        var file = Path.Combine(dir, "a.pdf");
        File.WriteAllText(file, "x");
        var stored = UploadStorage.MakeStoredPath("resumes", "u1", "a.pdf");

        Assert.True(_storage.Exists(stored));
        _storage.Delete(stored);
        Assert.False(_storage.Exists(stored));
        _storage.Delete(stored); // no-op when already gone
    }

    private sealed class FakeEnv : IWebHostEnvironment
    {
        public FakeEnv(string root)
        {
            ContentRootPath = root;
            WebRootPath = Path.Combine(root, "wwwroot");
            ContentRootFileProvider = new NullFileProvider();
            WebRootFileProvider = new NullFileProvider();
        }
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "InternTrackAI.Tests";
        public string WebRootPath { get; set; }
        public IFileProvider WebRootFileProvider { get; set; }
        public string ContentRootPath { get; set; }
        public IFileProvider ContentRootFileProvider { get; set; }
    }
}
