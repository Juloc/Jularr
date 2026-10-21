using System.Text;
using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    // The one file input on another page: the volume upload of a manga, with its drop zone, its file and its remove button.
    [TestClass]
    public sealed class FileInputTests
    {
        [TestMethod]
        [Timeout(300_000)]
        public async Task VolumeUpload_ShowsTheFileInput_AcceptsAVolume_AndRefusesAnotherKind()
        {
            await using var jularr = await JularrInstance.StartAsync();
            await using var session = await BrowserSession.StartAsync(jularr);
            var page = session.Page;

            try
            {
                await Owner.SetUpAndSignInAsync(page);
                await jularr.RunSqlAsync("""
                    INSERT INTO "Works" ("Id", "MediaTypeId", "MediaSubtypeId", "CanonicalTitle") VALUES (70, 3, 2, 'Blue Sea');
                    UPDATE "InstanceModules" SET "IsEnabled" = TRUE WHERE "Key" = 'Uploads';
                    """);
                await page.GotoAsync("/Admin/Works/70/Upload");
                var input = page.Locator(".file-input[data-file-ready]");
                await Assertions.Expect(input.Locator(".file-input__zone")).ToBeVisibleAsync();
                await Assertions.Expect(input.Locator(".file-input__limit")).ToHaveTextAsync("CBZ, CBR, CB7, EPUB, PDF · up to 2.0 GB");
                await Assertions.Expect(page.Locator("main label", new() { HasText = "Volume file" })).ToBeVisibleAsync();
                await Evidence.SaveAsync(page, "file-input-empty");

                // A volume is shown with its name and size; the field behind it carries it in the form.
                var field = input.Locator("input[type=file]");
                await field.SetInputFilesAsync(new FilePayload { Name = "Blue Sea v03.cbz", MimeType = "application/octet-stream", Buffer = Encoding.ASCII.GetBytes(new string('p', 4200)) });
                await Assertions.Expect(input.Locator("[data-file-name]")).ToHaveTextAsync("Blue Sea v03.cbz");
                await Assertions.Expect(input.Locator("[data-file-size]")).ToHaveTextAsync("4.2 KB");
                Assert.AreEqual(1, await field.EvaluateAsync<int>("field => field.files.length"));
                await Evidence.SaveAsync(page, "file-input-chosen");

                // Dropping another kind of file is refused with its reason and takes the first one out.
                await field.SetInputFilesAsync(new FilePayload { Name = "setup.exe", MimeType = "application/octet-stream", Buffer = [1, 2, 3] });
                await Assertions.Expect(input.Locator("[data-file-error]")).ToHaveTextAsync("Only CBZ, CBR, CB7, EPUB, PDF files are accepted.");
                Assert.AreEqual(0, await field.EvaluateAsync<int>("field => field.files.length"));
                await Evidence.SaveAsync(page, "file-input-refused");

                // The remove button leaves the page as it was.
                await field.SetInputFilesAsync(new FilePayload { Name = "v1.pdf", MimeType = "application/pdf", Buffer = [37, 80, 68, 70] });
                await input.GetByRole(AriaRole.Button, new() { Name = "Remove file" }).ClickAsync();
                await Assertions.Expect(input.Locator("[data-file-chosen]")).ToBeHiddenAsync();
                Assert.AreEqual(0, await field.EvaluateAsync<int>("field => field.files.length"));
                CollectionAssert.AreEqual(Array.Empty<string>(), session.Problems.ToArray());
            }
            catch (Exception exception) when (exception is not AssertInconclusiveException)
            {
                await Evidence.SaveAsync(page, "failure-file-input");
                throw;
            }
        }
    }
}
