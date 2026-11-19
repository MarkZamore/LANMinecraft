using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using CmlLib.Core.Files;

namespace Minecraft.Tests;

public sealed class OfficialClientDownloadTests
{
    [Fact]
    public async Task MissingClientUsesOfficialBytesAndTheSharedCacheIsVerifiedBeforeReuse()
    {
        var root = Path.Combine(Path.GetTempPath(), "official-client-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            byte[] original = [1, 2, 3];
            using var source = new ClientSource(original);
            using var http = new HttpClient(source);
            var path = Path.Combine(root, "versions", "fixture", "fixture.jar");
            var installer = new PortableGameInstaller(http, root, Path.Combine(root, "temp"), null, "Minecraft");
            var artifact = new GameFile("fixture.jar")
            {
                Path = path,
                Url = "https://piston-data.mojang.com/v1/objects/fixture/client.jar",
                Hash = Convert.ToHexString(SHA1.HashData(original)),
                Size = original.Length
            };

            await PackRuntimeService.InstallOfficialClientAsync(installer, artifact, default);
            Assert.Equal(original, File.ReadAllBytes(path));
            await PackRuntimeService.InstallOfficialClientAsync(installer, artifact, default);
            Assert.Equal(1, source.Requests);

            File.WriteAllBytes(path, [3, 2, 1]);
            await PackRuntimeService.InstallOfficialClientAsync(installer, artifact, default);
            Assert.Equal(2, source.Requests);
            Assert.Equal(original, File.ReadAllBytes(path));

            var untrusted = new GameFile("fixture.jar")
            {
                Path = path,
                Url = "https://untrusted.example/client.jar",
                Hash = artifact.Hash,
                Size = artifact.Size
            };
            Assert.Throws<InvalidDataException>(() =>
            {
                _ = PackRuntimeService.InstallOfficialClientAsync(installer, untrusted, default);
            });
            Assert.Equal(2, source.Requests);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class ClientSource(byte[] original) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("piston-data.mojang.com", request.RequestUri?.Host);
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(original) });
        }
    }
}
