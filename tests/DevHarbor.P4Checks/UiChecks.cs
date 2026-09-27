using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.IO;
using System.Diagnostics;
using DevHarbor.Desktop;
using DevHarbor.Models;
internal static partial class Program
{
    private static void RunUi()
    {
        var errors=new BindingErrors();PresentationTraceSources.DataBindingSource.Listeners.Add(errors);
        RenderOptions.ProcessRenderMode=System.Windows.Interop.RenderMode.SoftwareOnly;
        // Synchronous UI checks avoid changing actual app consent or sending HTTP requests.
        Check("connections-default-off-config-and-render",()=>
        {
            var connection=new AgentConnection();var window=new ConnectionsWindow(connection,new StorageViewModel());
            Require(!connection.Hub.IsSharing,"Sharing enabled by opening window");
            Require(File.Exists(McpLaunch.Resolve().Arguments.Single()),"Configuration points at missing server assembly");
            Require(((TextBox)window.FindName("ConfigText")).Text.Contains("[mcp_servers.devharbor]"),"Config missing");Render(window,"connections",984,770);window.Close();
        }).GetAwaiter().GetResult();
        Check("model-metrics-and-unknown-version-ui",()=>
        {
            using var session=new OllamaSessions(new OllamaFixture());var window=new ModelSessionsWindow(session);
            window.Present(new("Ready",OllamaSessions.VerifiedVersion,[new("fixture:latest",Digest,2048,1024,DateTimeOffset.UtcNow.AddMinutes(5))],DateTimeOffset.UtcNow,4096));
            var table=(DataGrid)window.FindName("ModelsTable");table.SelectedIndex=0;Require(((Button)window.FindName("UnloadButton")).IsEnabled,"Verified model unavailable");Render(window,"models",1004,720);
            window.Present(new("ReadOnlyVersion","99.0.0",[],DateTimeOffset.UtcNow,null));Require(!((Button)window.FindName("UnloadButton")).IsEnabled,"Unknown version actionable");window.Close();
        }).GetAwaiter().GetResult();
        Check("unload-confirmation-default-deny-and-expiry-render",()=>
        {
            var clock=new FakeTime();using var session=new OllamaSessions(new OllamaFixture(),clock:clock);
            // Fixture handler completes synchronously; no UI-thread network/blocking request occurs.
            var observation=session.ObserveAsync().GetAwaiter().GetResult();var plan=session.PrepareUnloadAsync(observation.Models[0]).GetAwaiter().GetResult();
            var window=new UnloadApprovalWindow(session,plan);var button=(Button)window.FindName("ConfirmButton");
            Require(!button.IsEnabled&&!button.IsDefault&&window.Grant==null,"Automatic approval");Render(window,"unload",684,490);
            ((CheckBox)window.FindName("Acknowledged")).IsChecked=true;window.UpdateConfirmation();Require(button.IsEnabled&&window.Grant==null,"Checkbox issued grant");
            clock.Advance(TimeSpan.FromSeconds(31));window.UpdateConfirmation();Require(!button.IsEnabled,"Expired approval enabled");window.Close();
        }).GetAwaiter().GetResult();
        Check("p4-wpf-bindings-resolve",()=>Require(errors.Messages.Count==0,"WPF binding errors: "+string.Join(";",errors.Messages))).GetAwaiter().GetResult();
        PresentationTraceSources.DataBindingSource.Listeners.Remove(errors);
    }
    private sealed class BindingErrors:TraceListener
    {
        internal List<string> Messages { get; }=[];
        public override void Write(string? value){if(!string.IsNullOrWhiteSpace(value))Messages.Add(value);}
        public override void WriteLine(string? value)=>Write(value);
    }
    private static void Render(Window window,string name,int width,int height)
    {
        var content=(FrameworkElement)window.Content;content.Measure(new Size(width,height));content.Arrange(new Rect(0,0,width,height));content.UpdateLayout();
        var image=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);image.Render(content);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));using var stream=File.Create(Path.Combine(root,name+".png"));encoder.Save(stream);
    }
}
