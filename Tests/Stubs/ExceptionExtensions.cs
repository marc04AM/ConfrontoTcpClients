// Extension method Format() usata da FaelTcpClient.
// Definita in Sistec.Core.Utils (namespace importato da Fael)
// e in Sistec.Asyril.Utils (namespace importato da Mb, ma Mb non la usa).

namespace Sistec.Core.Utils;

public static class ExceptionExtensions
{
    public static string Format(this Exception e)
    {
        var message = $"{e.Message}\n{e.StackTrace}";
        if (e.InnerException != null)
            message += $"\ninner exception:{e.InnerException.Message}\n{e.InnerException.StackTrace}";
        return message;
    }
}
