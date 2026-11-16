using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.VisualStudio.Debugger.DebugAdapterHost.Interfaces;
using Newtonsoft.Json.Linq;

namespace Vistudio
{
    [ComVisible(true), Guid("950e9dc5-32b7-4ffa-b3e0-073b9bdfe92c")]
    public sealed class KotlinAdapterLauncher : IAdapterLauncher
    {
        public void Initialize(IDebugAdapterHostContext context) { }
        public void UpdateLaunchOptions(IAdapterLaunchInfo launchInfo) { }
        public ITargetHostProcess LaunchAdapter(IAdapterLaunchInfo launchInfo, ITargetHostInterop targetInterop)
        {
            var launch = JObject.Parse(launchInfo.LaunchJson);
            var port = (int?)launch["vistudioPort"] ??
                throw new InvalidOperationException("Missing Kotlin DAP endpoint. Start this target with F5 from Open Folder.");
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            {
                // The host calls this synchronous contract after LaunchDebugTargets4 has returned.
                // Do not transfer sockets or state through static fields across that boundary: the launch JSON names the
                // port, and the id under which this connection reports what it sees of the launch (KotlinDebugMonitor).
#pragma warning disable VSTHRD002
                return KotlinDebugTransport.ConnectAsync(port, () => { }, timeout.Token, KotlinDebugMonitor.Find((string?)launch["vistudioSession"])).GetAwaiter().GetResult();
#pragma warning restore VSTHRD002
            }
        }
    }
}
