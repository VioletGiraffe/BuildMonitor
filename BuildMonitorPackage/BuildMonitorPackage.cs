﻿using System;
using System.ComponentModel.Design;
using System.Runtime.InteropServices;
using BuildMonitor;
using BuildMonitor.Domain;
using BuildMonitor.UI;
using EnvDTE;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Shell;
using System.Data.SqlClient;
using System.Data;
using System.Security.Principal;
using Microsoft.VisualStudio.Settings;
using Microsoft.VisualStudio.Shell.Settings;
using System.Threading;
using System.Threading.Tasks;

namespace BuildMonitorPackage
{
    [InstalledProductRegistration("#110", "#112", "1.0", IconResourceID = 400)]
    [Guid(GuidList.guidBuildMonitorPackagePkgString)]
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [ProvideAutoLoad(VSConstants.UICONTEXT.SolutionExists_string, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [ProvideOptionPage(typeof(SettingsPage), "Build Monitor", "General", 0, 0, true)]
    sealed class BuildMonitorPackage : AsyncPackage, IVsUpdateSolutionEvents2
    {
        DTE dte;
        BuildMonitor.Domain.Monitor monitor;
        DataAdjusterWithLogging dataAdjuster;
        BuildMonitor.Domain.Solution solution;

        IVsSolutionBuildManager2 sbm;
        uint updateSolutionEventsCookie;
        SolutionEvents events;
        IVsSolution2 vsSolution;
        OutputWindowWrapper output;

        protected override async System.Threading.Tasks.Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            // Switches to the UI thread, which most of this package requires. Even joining the main thread here improves 
            // the load time of the package, and it stops a warning popping up when you load vs2019 with the package installed.
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            await base.InitializeAsync(cancellationToken, progress);

            output = new OutputWindowWrapper(this);

            SettingsManager settingsManager = new ShellSettingsManager(ServiceProvider.GlobalProvider);
            WritableSettingsStore settingsStore = settingsManager.GetWritableSettingsStore(SettingsScope.UserSettings);
            Settings.Instance = new Settings(settingsStore);

            var factory = new BuildFactory();
            var repository = new BuildRepository(Settings.Instance.RepositoryPath);

            monitor = new BuildMonitor.Domain.Monitor(factory, repository);
            dataAdjuster = new DataAdjusterWithLogging(repository, output.WriteLine);

            //if invalid data, adjust it
            dataAdjuster.Adjust();


            // Get solution build manager
            sbm = ServiceProvider.GlobalProvider.GetService(typeof(SVsSolutionBuildManager)) as IVsSolutionBuildManager2;
            if (sbm != null)
            {
                sbm.AdviseUpdateSolutionEvents(this, out updateSolutionEventsCookie);
            }

            // Must hold a reference to the solution events object or the events wont fire, garbage collection related
            events = GetDTE().Events.SolutionEvents;
            events.Opened += Solution_Opened;
            GetDTE().Events.BuildEvents.OnBuildBegin += Build_Begin;

            output.WriteLine("Build monitor initialized");
            output.WriteLine("Path to persist data: {0}", Settings.Instance.RepositoryPath);

            monitor.SolutionBuildFinished = b =>
            {
                output.Write("[{0}] Time Elapsed: {1} \t\t", b.SessionBuildCount, b.SolutionBuildTime.ToTime());
                output.WriteLine("Session build time: {0}\n", b.SessionMillisecondsElapsed.ToTime());
                output.WriteLine("Rebuild All: {0}\n", b.SolutionBuild.IsRebuildAll);
               // System.Threading.Tasks.Task.Factory.StartNew(() => SaveToDatabase(b));
            };

            monitor.ProjectBuildFinished = b => output.WriteLine(" - {0}\t-- {1} --", b.MillisecondsElapsed.ToTime(), b.ProjectName);
            AnalyseBuildTimesCommand.Initialize(this);
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);

            // Unadvise all events
            if (sbm != null && updateSolutionEventsCookie != 0)
                sbm.UnadviseUpdateSolutionEvents(updateSolutionEventsCookie);
        }

        //void SaveToDatabase(SolutionBuildData b)
        //{
        //    try
        //    {
        //        var conn = new SqlConnection("Server=kl-sql-005;DataBase=RESSoftware;Integrated Security=SSPI");
        //        conn.Open();
        //        SqlCommand cmd = new SqlCommand("dbo.AddBuildTime", conn);
        //        cmd.Parameters.AddWithValue("IsRebuildAll", b.SolutionBuild.IsRebuildAll ? 1 : 0);
        //        cmd.Parameters.AddWithValue("SolutionName", b.SolutionName);
        //        cmd.Parameters.AddWithValue("BuildDateTime", DateTime.Now);
        //        cmd.Parameters.AddWithValue("TimeInMilliseconds", b.SolutionBuildTime);
        //        cmd.Parameters.AddWithValue("NT4Name", WindowsIdentity.GetCurrent().Name);
        //        cmd.CommandType = CommandType.StoredProcedure;
        //        cmd.ExecuteNonQuery();
        //        if (conn != null) conn.Close();
        //    }
        //    catch // ignore exceptions, its not a big problem if we can't log the build time
        //    { }
        //}

        void Solution_Opened()
        {
            solution = new BuildMonitor.Domain.Solution { Name = GetSolutionName() };
            output.WriteLine("\nSolution loaded:  \t{0}", solution.Name);
            output.WriteLine(new string('-', 60));
        }

        #region Get objects from vs

        DTE GetDTE()
        {
            if (dte == null)
            {
                var serviceContainer = this as IServiceContainer;
                dte = serviceContainer.GetService(typeof(SDTE)) as DTE;
            }
            return dte;
        }

        void SetVsSolution()
        {
            if (vsSolution == null)
                vsSolution = ServiceProvider.GlobalProvider.GetService(typeof(SVsSolution)) as IVsSolution2;
        }

        string GetSolutionName()
        {
            SetVsSolution();
            object solutionName;
            vsSolution.GetProperty((int)__VSPROPID.VSPROPID_SolutionBaseName, out solutionName);
            return (string)solutionName;
        }

        IProject GetProject(IVsHierarchy pHierProj)
        {
            SetVsSolution();
            object n;
            pHierProj.GetProperty((uint)VSConstants.VSITEMID.Root, (int)__VSHPROPID.VSHPROPID_Name, out n);
            var name = n as string;

            vsSolution.GetGuidOfProject(pHierProj, out Guid id);

            return new BuildMonitor.Domain.Project { Name = name, Id = id };
        }

        #endregion

        // this event is called on build begin and let's us find out whether it is a full rebuild or a partial
        void Build_Begin(vsBuildScope scope, vsBuildAction action)
        {
            monitor.SetIsRebuildAll(action == vsBuildAction.vsBuildActionRebuildAll);
        }

        int IVsUpdateSolutionEvents.UpdateSolution_Begin(ref int pfCancelUpdate)
        {
            // This method is called when the entire solution starts to build.
            monitor.SolutionBuildStart(solution);

            return VSConstants.S_OK;
        }

        int IVsUpdateSolutionEvents2.UpdateProjectCfg_Begin(IVsHierarchy pHierProj, IVsCfg pCfgProj, IVsCfg pCfgSln, uint dwAction, ref int pfCancel)
        {
            // This method is called when a specific project begins building.
            var project = GetProject(pHierProj);
            monitor.ProjectBuildStart(project);

            return VSConstants.S_OK;
        }

        int IVsUpdateSolutionEvents2.UpdateProjectCfg_Done(IVsHierarchy pHierProj, IVsCfg pCfgProj, IVsCfg pCfgSln, uint dwAction, int fSuccess, int fCancel)
        {
            // This method is called when a specific project finishes building.
            var project = GetProject(pHierProj);
            monitor.ProjectBuildStop(project);

            return VSConstants.S_OK;
        }

        int IVsUpdateSolutionEvents.UpdateSolution_Done(int fSucceeded, int fModified, int fCancelCommand)
        {
            // This method is called when the entire solution is done building.
            monitor.SolutionBuildStop();

            return VSConstants.S_OK;
        }

        #region empty impl. of solution events interface, good example of Interface Segregation Principle violation

        int IVsUpdateSolutionEvents2.UpdateSolution_StartUpdate(ref int pfCancelUpdate) =>
            VSConstants.S_OK;

        int IVsUpdateSolutionEvents2.UpdateSolution_Cancel() =>
            VSConstants.S_OK;

        int IVsUpdateSolutionEvents2.OnActiveProjectCfgChange(IVsHierarchy pIVsHierarchy) =>
            VSConstants.S_OK;

        int IVsUpdateSolutionEvents2.UpdateSolution_Begin(ref int pfCancelUpdate) =>
            VSConstants.S_OK;

        int IVsUpdateSolutionEvents2.UpdateSolution_Done(int fSucceeded, int fModified, int fCancelCommand) =>
            VSConstants.S_OK;

        int IVsUpdateSolutionEvents.UpdateSolution_StartUpdate(ref int pfCancelUpdate) =>
            VSConstants.S_OK;

        int IVsUpdateSolutionEvents.UpdateSolution_Cancel() =>
            VSConstants.S_OK;

        int IVsUpdateSolutionEvents.OnActiveProjectCfgChange(IVsHierarchy pIVsHierarchy) =>
            VSConstants.S_OK;

        #endregion

    }
}