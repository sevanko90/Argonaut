using Argonaut.Engine.Settings;
using Argonaut.Features.Raw;
using Argonaut.Shell;
using Argonaut.Tests.Support;
using Argonaut.Ui.Documents;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Argonaut.Tests.Ui.Documents;

/// <summary>
/// One document replacing another of the same kind gets its own view. A recycling template hands
/// the outgoing document's view the new document without telling it, and the raw view went on
/// drawing the disposed document - blank, on every other file dropped in.
/// </summary>
public class FreshViewTemplateTests
{
    [Fact]
    public Task TextFilesOpenedOneAfterAnother_AreEachDrawnInTheirOwnView()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(FreshViewTemplateTests).Assembly);
        return session.Dispatch(async () =>
        {
            string dir = Directory.CreateTempSubdirectory("argonaut-fresh-view-").FullName;
            string a = Path.Combine(dir, "a.txt");
            string b = Path.Combine(dir, "b.txt");
            File.WriteAllText(a, string.Concat(Enumerable.Range(0, 200).Select(i => $"alpha line {i}\n")));
            File.WriteAllText(b, string.Concat(Enumerable.Range(0, 200).Select(i => $"bravo line {i}\n")));

            var shell = new MainWindowViewModel(SettingsStore.InMemory(), TestSchemas.Catalog(), _ => Task.FromResult(true));
            var host = new ContentControl();
            // Recycling, as a XAML DataTemplate is; the wrapper is what keeps it from reusing a view.
            host.DataTemplates.Add(new FreshViewTemplate
            {
                Template = new FuncDataTemplate<RawViewModel>((_, _) => new RawView(), supportsRecycling: true),
            });
            host.Bind(ContentControl.ContentProperty, new Binding(nameof(MainWindowViewModel.CurrentDocument)) { Source = shell });
            var window = new Window { Width = 900, Height = 600, Content = host };
            window.Show();

            var surfaces = new List<RawTextSurface>();
            try
            {
                foreach (string path in new[] { a, b, a })
                {
                    await shell.OpenPathAsync(path);
                    var document = Assert.IsType<RawViewModel>(shell.CurrentDocument);
                    await document.IndexingTask;
                    for (int i = 0; i < 5; i++)
                    {
                        await Task.Delay(20);
                        Dispatcher.UIThread.RunJobs();
                        window.UpdateLayout();
                    }

                    var surface = window.GetVisualDescendants().OfType<RawTextSurface>().Single();
                    Assert.Same(document, surface.DataContext);
                    Assert.True(surface.RealizedRowCount > 0, $"{Path.GetFileName(path)} drew no rows");
                    surfaces.Add(surface);
                }
            }
            finally
            {
                await shell.CloseFileAsync();
                window.Close();
                Directory.Delete(dir, recursive: true);
            }

            Assert.Equal(surfaces.Count, surfaces.Distinct().Count());
            return true;
        }, CancellationToken.None);
    }
}
