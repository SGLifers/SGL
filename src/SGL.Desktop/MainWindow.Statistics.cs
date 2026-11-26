using System.Text.Json.Nodes;
using System.Windows;
using SGL.Core;
namespace SGL.Desktop;

public partial class MainWindow
{
    StatisticsCollection? statisticsCollection;
    int statisticsGeneration;
    readonly Dictionary<string,bool> statisticsOpenChoices=[];
    void InitializeStatistics()
    {
        StatisticsLogPath.Text=(string?)Preferences()["statisticsLogPath"] ?? StatisticsCollection.DefaultLog;
        Closed+=(_,_)=>StopStatistics();
    }
    void StatisticsPathSaved(object sender,RoutedEventArgs e)
    {
        if(!hostingReady)return;
        try
        {
            string path=Path.GetFullPath(StatisticsLogPath.Text.Trim());
            var prefs=Preferences();prefs["statisticsLogPath"]=path;SGL.Core.Json.Atomic(Path.Combine(dataRoot,"settings.json"),prefs.ToJsonString());
            StatisticsStatus.Text="Log location saved for the next lobby.";
        }
        catch{StatisticsStatus.Text="Choose a valid game log path.";}
    }
    void StartStatistics(string actor,string token)
    {
        if(statisticsCollection!=null)return;
        int generation=++statisticsGeneration;
        string endpoint=(string?)SGL.Core.Json.Read(Modes.Resource("statistics-service.json"))["apiUrl"] ?? "";
        statisticsCollection=new StatisticsCollection(dataRoot,actor,token,endpoint,StatisticsLogPath.Text,text=>
        {
            if(Dispatcher.HasShutdownStarted)return;
            _=Dispatcher.InvokeAsync(()=>{if(generation==statisticsGeneration)StatisticsStatus.Text=text+(string.IsNullOrWhiteSpace(endpoint)?" Results stay local until the stats service is configured.":"");});
        });
        statisticsCollection.Start();StatisticsStatus.Text="Stats session armed. Waiting for verified hosting evidence…";
    }
    void StopStatistics()
    {
        statisticsGeneration++;var old=statisticsCollection;statisticsCollection=null;
        if(old!=null)_=Task.Run(async()=>await old.DisposeAsync());
    }
    void ResetStatisticsChoice()
    {
        StopStatistics();statisticsOpenChoices.Clear();HostingCollectStats.IsChecked=true;
        StatisticsStatus.Text="Collect stats is enabled by default for a new lobby.";
    }
}
