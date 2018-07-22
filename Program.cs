using System.Threading;

namespace TcpProxy
{
    class Program
    {
        static void Main(string[] args)
        {
            var proxy = new Proxy(1080, "towel.blinkenlights.nl", 23);
            proxy.Begin(new CancellationToken(false));
        }
    }
}
