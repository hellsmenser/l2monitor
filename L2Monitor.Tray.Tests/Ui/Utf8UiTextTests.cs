using System.Text;
using Xunit;

namespace L2Monitor.Tray.Tests.Ui;

public sealed class Utf8UiTextTests
{
    [Fact]
    public void MainWindowXaml_DoesNotContainMojibakeMarkers()
    {
        var repositoryRoot = FindRepositoryRoot();
        var xamlPath = Path.Combine(repositoryRoot, "L2Monitor.Tray", "Ui", "MainWindow.xaml");
        var text = File.ReadAllText(xamlPath, new UTF8Encoding(false, true));

        Assert.DoesNotContain("РћС", text, StringComparison.Ordinal);
        Assert.DoesNotContain("РїС", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Р»С", text, StringComparison.Ordinal);
        Assert.DoesNotContain('\uFFFD', text);
    }

    [Fact]
    public void TraySourceFiles_DoNotContainMojibakeMarkers()
    {
        var repositoryRoot = FindRepositoryRoot();
        var files = Directory.EnumerateFiles(Path.Combine(repositoryRoot, "L2Monitor.Tray"), "*.cs", SearchOption.AllDirectories);

        foreach (var file in files)
        {
            var text = File.ReadAllText(file, new UTF8Encoding(false, true));
            Assert.DoesNotContain("Рћ", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Рџ", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Рµ", text, StringComparison.Ordinal);
            Assert.DoesNotContain('\uFFFD', text);
        }
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "L2Monitor.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the L2Monitor agent repository root.");
    }
}
