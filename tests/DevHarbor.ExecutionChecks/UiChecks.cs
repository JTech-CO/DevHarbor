using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DevHarbor.Desktop;
using DevHarbor.Execution;
internal static partial class Program
{
    private static void RunUiChecks()
    {
        RenderOptions.ProcessRenderMode=System.Windows.Interop.RenderMode.SoftwareOnly;
        var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
        app.Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri("/DevHarbor.Desktop;component/Theme.xaml",UriKind.Relative)});
        var errors=new UiBindingErrors();PresentationTraceSources.DataBindingSource.Listeners.Add(errors);
        Check("approval-default-deny-target-display-and-expiry",()=>
        {
            var time=new FakeTime();using var service=Service(time);var plan=service.CreateSamplePlan();var window=new ApprovalWindow(plan,service.Broker);
            var approve=(Button)window.FindName("ConfirmButton");var acknowledged=(CheckBox)window.FindName("Acknowledged");
            Require(!approve.IsEnabled&&!approve.IsDefault&&acknowledged.IsChecked!=true&&window.Grant==null,"Implicit approval");
            Require(((TextBlock)window.FindName("SourceText")).Text==plan.Source&&((TextBlock)window.FindName("DestinationText")).Text==plan.Destination,"Incorrect target display");
            Render(window,"approval",760,750);acknowledged.IsChecked=true;window.UpdateConfirmation();Require(approve.IsEnabled&&window.Grant==null,"Checkbox issued grant");
            time.Advance(TimeSpan.FromMinutes(3));window.UpdateConfirmation();Require(!approve.IsEnabled&&window.Grant==null,"Expired confirmation enabled");
            window.Close();Require(File.Exists(plan.Source),"Closing dialog moved sample");
        });
        Check("recovery-history-and-blocked-cache-render",()=>
        {
            using var service=Service();var plan=service.CreateSamplePlan();Require(service.Execute(plan,service.Broker.IssueFromLocalConfirmation(plan)).State=="Held","Fixture failed");
            var window=new CleanupWindow(service,new CleanupAssessment("pip",@"C:\DevHarbor-fixture\pip",false,["사용 중인 파일·메모리 매핑 검증이 남아 있습니다.","재생성 출처와 인증·네트워크 복구 가능성을 확인하지 않았습니다.","실제 캐시 정리 기능이 비활성화되어 있습니다."]));
            window.PresentHistory(service.ReadHistory());var grid=(DataGrid)window.FindName("HistoryTable");grid.SelectedIndex=0;
            Require(((Button)window.FindName("RestoreButton")).IsEnabled,"Held sample unavailable for review");
            Render(window,"history",1040,770);
            var restore=service.CreateRestorePlan(plan.Id,"restored-sample.txt");var confirmation=new ApprovalWindow(restore,service.Broker);
            Require(((TextBlock)confirmation.FindName("DestinationText")).Text.EndsWith("restored-sample.txt"),"Alternate target not shown");
            Render(confirmation,"restore",760,750);confirmation.Close();
            window.PresentHistory([new HistoryEntry("ambiguous","HoldSample","NeedsReview",plan.Source,plan.Destination,"AmbiguousFilesystemState",DateTimeOffset.UtcNow)]);grid.SelectedIndex=0;
            Require(!((Button)window.FindName("RestoreButton")).IsEnabled,"Ambiguous restore enabled");window.Close();
        });
        Check("approval-history-bindings-resolve",()=>Require(errors.Messages.Count==0,"Binding errors: "+string.Join(";",errors.Messages)));
        PresentationTraceSources.DataBindingSource.Listeners.Remove(errors);app.Shutdown();
    }
    private sealed class UiBindingErrors : TraceListener
    {
        internal List<string> Messages { get; } = [];
        public override void Write(string? message) { if(!string.IsNullOrWhiteSpace(message))Messages.Add(message); }
        public override void WriteLine(string? message)=>Write(message);
    }
    private static void Render(Window window,string name,int width,int height)
    {
        var content=(FrameworkElement)window.Content;content.Measure(new Size(width,height));content.Arrange(new Rect(0,0,width,height));content.UpdateLayout();
        var bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);bitmap.Render(content);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var stream=File.Create("artifacts/p3/"+name+".png");encoder.Save(stream);
    }
}
