using System.IO;
using System.Text;

namespace InkCanvas.Plugins.LegacyImport;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 2) return 2;
        try
        {
            var page = LegacyPage.Read(args[0]);
            using var output = new FileStream(args[1], FileMode.CreateNew, FileAccess.Write, FileShare.None);
            page.Write(output);
            return 0;
        }
        catch (Exception ex)
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.Error.WriteLine(ex.GetBaseException().Message);
            return 1;
        }
    }
}
