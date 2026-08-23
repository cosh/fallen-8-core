// MIT License
//
// ProviderDescriptorSnapshotTest.cs
//
// Copyright (c) 2011-2026 Henning Rauch
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
//
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// Drift guard for the shipped provider descriptors, in the same shape as the pinned
    /// OpenAPI snapshot: <c>features/done/integrations/provider-descriptors.json</c> is what
    /// <c>GET /integration/providers</c> serves, and this test fails when the runtime no longer
    /// serves it.
    ///
    /// <para>The snapshot has a second consumer that cannot check itself: the docs screenshot capture
    /// (<c>fallen-8-web-ui/e2e/screenshot-integrations.spec.ts</c>) serves it as the stub for the
    /// proxied sidecar, because that sidecar is a separate deployable the e2e webServer does not
    /// start. A changed descriptor therefore restates the published Integrations image, which is why
    /// the failure below names the recapture.</para>
    ///
    /// <para>It is the ROUTE'S JSON rather than a serialization of the descriptor objects: the
    /// Studio consumes the route, so a snapshot taken past the route would pin a shape no client
    /// ever sees.</para>
    /// </summary>
    [TestClass]
    public class ProviderDescriptorSnapshotTest
    {
        private const String ProvidersRoute = "/integration/providers";

        private const String UpdateVariable = "F8_UPDATE_PROVIDER_DESCRIPTOR_SNAPSHOT";

        private const String RegenerateHint =
            "Regenerate with `powershell -File scripts/update-provider-descriptor-snapshot.ps1`, review the " +
            "printed diff, and recapture docs/src/assets/images/screen-integrations.png " +
            "(F8_SCREENSHOT=1 npx playwright test e2e/screenshot-integrations.spec.ts) so the published " +
            "image still shows what the runtime serves.";

        /// <summary>The fields the Studio's <c>IntegrationProvider</c> type models, per descriptor.</summary>
        private static readonly HashSet<String> DescriptorFields = new HashSet<String>(StringComparer.Ordinal)
        {
            "id", "displayName", "description", "settings", "entityKinds", "claimTypes", "relationTypes",
            "canObserveCompleteState", "readOnly", "entitySummaryTemplate",
        };

        /// <summary>The fields the Studio's <c>IntegrationSetting</c> type models, per setting.</summary>
        private static readonly HashSet<String> SettingFields = new HashSet<String>(StringComparer.Ordinal)
        {
            "key", "label", "kind", "required", "help", "defaultValue", "accept",
        };

        /// <summary>The Studio's <c>SettingKind</c> union. A form has a control for these and nothing else.</summary>
        private static readonly HashSet<String> SettingKinds = new HashSet<String>(StringComparer.Ordinal)
        {
            "Text", "Number", "Boolean", "Url", "Credential", "File",
        };

        private sealed class RuntimeFactory : WebApplicationFactory<NoSQL.GraphDB.Integrations.Program>
        {
        }

        private static String SnapshotPath()
        {
            return Path.Combine(TestRepo.Root(), "features", "done", "integrations", "provider-descriptors.json");
        }

        /// <summary>
        /// Both sides of the comparison pass through here, so indentation, escaping and line endings
        /// cannot fail the test: only a changed field, value or order can. The relaxed encoder is what
        /// keeps an apostrophe in a help text readable in the committed file rather than a numeric
        /// escape; both sides are encoded the same way, so it changes no verdict.
        /// </summary>
        private static String Canonical(String json)
        {
            using var document = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,

                // Pinned rather than left to the platform, so regenerating on Linux does not rewrite
                // every line of a file written on Windows.
                NewLine = "\n",
            });
        }

        private static async Task<String> ServedDescriptors()
        {
            using var factory = new RuntimeFactory();
            using var client = factory.CreateClient();

            var response = await client.GetAsync(ProvidersRoute);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode,
                "the runtime serves its provider catalog at " + ProvidersRoute);

            return await response.Content.ReadAsStringAsync();
        }

        [TestMethod]
        public async Task ServedDescriptors_MatchTheCommittedSnapshot()
        {
            var path = SnapshotPath();
            var served = Canonical(await ServedDescriptors());

            if (String.Equals(Environment.GetEnvironmentVariable(UpdateVariable), "1", StringComparison.Ordinal))
            {
                File.WriteAllText(path, served + "\n", new UTF8Encoding(false));
                Assert.Inconclusive(
                    "snapshot rewritten from the live route: " + path + ". Nothing was judged in update mode; " +
                    "review the diff and run the suite again without " + UpdateVariable + ".");
            }

            Assert.IsTrue(File.Exists(path), "the pinned provider-descriptor snapshot exists at " + path);

            var pinned = Canonical(File.ReadAllText(path));
            if (String.Equals(pinned, served, StringComparison.Ordinal))
            {
                return;
            }

            Assert.Fail(
                "the shipped provider descriptors no longer match the pinned snapshot " + path +
                ", which the docs screenshot fixture serves as its stub, so the published Integrations " +
                "image now shows a form the runtime does not offer.\n" + FirstDifference(pinned, served) +
                "\n" + RegenerateHint);
        }

        /// <summary>
        /// The first line the two differ on, because the whole document in the message buries the point:
        /// a reader needs the field that moved, not four kilobytes of the fields that did not.
        /// </summary>
        private static String FirstDifference(String pinned, String served)
        {
            var pinnedLines = pinned.Replace("\r\n", "\n").Split('\n');
            var servedLines = served.Replace("\r\n", "\n").Split('\n');

            for (var index = 0; index < Math.Max(pinnedLines.Length, servedLines.Length); index++)
            {
                var left = index < pinnedLines.Length ? pinnedLines[index] : "(end of snapshot)";
                var right = index < servedLines.Length ? servedLines[index] : "(end of served list)";

                if (!String.Equals(left, right, StringComparison.Ordinal))
                {
                    return "first difference at line " + (index + 1) + ":\n  snapshot: " + left.Trim() +
                           "\n  served:   " + right.Trim();
                }
            }

            return "no line differs, so the two differ only in trailing content.";
        }

        [TestMethod]
        public void Snapshot_CarriesOnlyFieldsTheStudioModels()
        {
            var path = SnapshotPath();
            Assert.IsTrue(File.Exists(path), "the pinned provider-descriptor snapshot exists at " + path);

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            Assert.AreEqual(JsonValueKind.Array, document.RootElement.ValueKind,
                "the route serves an array of descriptors");
            Assert.IsTrue(document.RootElement.GetArrayLength() > 0, "the snapshot pins the shipped providers");

            foreach (var descriptor in document.RootElement.EnumerateArray())
            {
                var id = descriptor.TryGetProperty("id", out var idValue) ? idValue.GetString() : null;
                Assert.IsFalse(String.IsNullOrEmpty(id), "every descriptor names its provider id");

                foreach (var field in descriptor.EnumerateObject())
                {
                    Assert.IsTrue(DescriptorFields.Contains(field.Name),
                        "descriptor '" + id + "' carries the field '" + field.Name + "', which the Studio's " +
                        "IntegrationProvider type (fallen-8-web-ui/src/api/types.ts) does not model: add it " +
                        "there, or the screen and the docs fixture silently drop it.");
                }

                foreach (var required in new[] { "canObserveCompleteState", "readOnly" })
                {
                    Assert.IsTrue(descriptor.TryGetProperty(required, out var flag) &&
                                  (flag.ValueKind == JsonValueKind.True || flag.ValueKind == JsonValueKind.False),
                        "descriptor '" + id + "' declares '" + required + "' as a boolean, which the Studio " +
                        "type requires.");
                }

                Assert.IsTrue(descriptor.TryGetProperty("settings", out var settings) &&
                              settings.ValueKind == JsonValueKind.Array,
                    "descriptor '" + id + "' declares its settings as data, which is what the form renders from.");

                foreach (var setting in settings.EnumerateArray())
                {
                    var key = setting.TryGetProperty("key", out var keyValue) ? keyValue.GetString() : null;
                    Assert.IsFalse(String.IsNullOrEmpty(key), "every setting of '" + id + "' names its key");

                    foreach (var field in setting.EnumerateObject())
                    {
                        Assert.IsTrue(SettingFields.Contains(field.Name),
                            "setting '" + key + "' of '" + id + "' carries the field '" + field.Name +
                            "', which the Studio's IntegrationSetting type does not model.");
                    }

                    var kind = setting.TryGetProperty("kind", out var kindValue) ? kindValue.GetString() : null;
                    Assert.IsTrue(kind != null && SettingKinds.Contains(kind),
                        "setting '" + key + "' of '" + id + "' declares kind '" + kind + "', which is not one " +
                        "of the kinds the Studio has a control for (" + String.Join(", ", SettingKinds) + ").");

                    Assert.IsTrue(setting.TryGetProperty("required", out var requiredFlag) &&
                                  (requiredFlag.ValueKind == JsonValueKind.True ||
                                   requiredFlag.ValueKind == JsonValueKind.False),
                        "setting '" + key + "' of '" + id + "' declares 'required' as a boolean.");
                }
            }
        }

        /// <summary>
        /// The drift guard only reaches the screenshot while the capture replays the snapshot: a fixture
        /// that declares its own descriptor objects again is outside everything asserted above, and its
        /// image can then contradict the runtime with nothing going red.
        /// </summary>
        [TestMethod]
        public void ScreenshotFixture_ServesTheSnapshotRatherThanItsOwnObjects()
        {
            var fixturePath = Path.Combine(TestRepo.Root(), "fallen-8-web-ui", "e2e",
                "screenshot-integrations.spec.ts");
            Assert.IsTrue(File.Exists(fixturePath), "the Integrations screenshot capture exists at " + fixturePath);

            var fixture = File.ReadAllText(fixturePath);

            Assert.IsTrue(fixture.Contains("features/done/integrations/provider-descriptors.json", StringComparison.Ordinal),
                "the Integrations screenshot capture no longer reads the pinned snapshot " +
                "features/done/integrations/provider-descriptors.json, so nothing keeps the published image " +
                "in step with the shipped descriptors: " + fixturePath);

            Assert.IsFalse(fixture.Contains("displayName:", StringComparison.Ordinal),
                "the Integrations screenshot capture declares descriptor fields of its own again (" +
                fixturePath + "). Hand-copied descriptors are what let the published image show a UniFi " +
                "site filter the product refuses to have; serve the pinned snapshot instead.");
        }
    }
}
