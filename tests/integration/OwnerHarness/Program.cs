using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Vistudio;

class Program
{
    static void Send(object message) { Console.WriteLine(JsonConvert.SerializeObject(message)); Console.Out.Flush(); }
    static async Task Main(string[] args)
    {
        var source = File.ReadAllText(args[0]);
        var completion = KotlinSemanticCompletion.Create(args[0], source, int.Parse(args[1]), args[2])!;
        var result = await completion.RequestAsync((method, arguments, token) =>
        {
            Send(new { kind = "request", method, arguments });
            return Task.FromResult(JToken.Parse(Console.ReadLine()!));
        }, (method, arguments) =>
        {
            Send(new { kind = "notify", method, arguments });
            Console.ReadLine();
            return Task.CompletedTask;
        }, CancellationToken.None);
        Send(new { kind = "result", items = result.Select(x => new { x.Name, x.Insertion, x.Description }) });
    }
}
