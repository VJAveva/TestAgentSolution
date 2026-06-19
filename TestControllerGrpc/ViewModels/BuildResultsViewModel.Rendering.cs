using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Mail;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

using TestControllerGrpc.Views.Dialogs;

namespace TestControllerGrpc.ViewModels;

public partial class BuildResultsViewModel : ObservableObject
{
    // ???????????????????????????????????????????????????????????????
    // Private helpers � Tree building
    // ???????????????????????????????????????????????????????????????

    private void BuildResultsTree(BuildNode buildNode)
    {
        ResultsTree.Clear();
        var treeNode = CreateBuildTreeNode(buildNode, isExpanded: true);
        ResultsTree.Add(treeNode);
    }

    private void BuildMultiBuildResultsTree()
    {
        ResultsTree.Clear();
        foreach (var buildNode in LoadedBuildNodes)
            ResultsTree.Add(CreateBuildTreeNode(buildNode, isExpanded: false));
    }

    private ResultsTreeNode CreateBuildTreeNode(BuildNode buildNode, bool isExpanded)
    {
        var treeNode = new ResultsTreeNode
        {
            Name = buildNode.BuildNumber,
            Total = buildNode.TotalTests,
            Passed = buildNode.PassedTests,
            Failed = buildNode.FailedTests,
            NotExecuted = buildNode.NotExecutedTests,
            PassRate = buildNode.PassRate,
            PassRateColor = GetRateColor(buildNode.PassRate),
            NodeLevel = "Build",
            ModifiedDate = buildNode.LatestRun,
            IsExpanded = isExpanded,
            ShowExportButtons = true,
        };

        treeNode.ExportHtmlCommand = new RelayCommand(() => ExportBuildToHtml(buildNode));
        treeNode.ExportCsvCommand = new RelayCommand(() => ExportBuildToCsv(buildNode));

        foreach (var uc in buildNode.UseCases)
        {
            var ucNode = new ResultsTreeNode
            {
                Name = uc.UseCaseName,
                Total = uc.Total,
                Passed = uc.Passed,
                Failed = uc.Failed,
                NotExecuted = uc.NotExecuted,
                PassRate = uc.PassRate,
                PassRateColor = GetRateColor(uc.PassRate),
                NodeLevel = "UseCase",
            };

            foreach (var tr in uc.FailedTests)
            {
                ucNode.Children.Add(new ResultsTreeNode
                {
                    Name = tr.TestName,
                    NodeLevel = "TestResult",
                    Outcome = tr.Outcome,
                    ErrorMessage = TruncateError(tr.ErrorMessage),
                    FullError = tr.ErrorMessage ?? "",
                    FullStackTrace = tr.StackTrace ?? "",
                });
            }

            treeNode.Children.Add(ucNode);
        }

        return treeNode;
    }

    private void BuildFlatList()
    {
        FlatResultsList.Clear();
        var hasAllBuildsRow = LoadedBuildNodes.Count > 1;

        if (hasAllBuildsRow)
        {
            var allTotal = LoadedBuildNodes.Sum(b => b.TotalTests);
            var allPassed = LoadedBuildNodes.Sum(b => b.PassedTests);
            var allFailed = LoadedBuildNodes.Sum(b => b.FailedTests);
            var allNotExe = LoadedBuildNodes.Sum(b => b.NotExecutedTests);
            var allRate = allTotal > 0 ? (double)allPassed / allTotal * 100 : 0;

            var allNode = new ResultsFlatNode
            {
                Name = $"All Builds ({LoadedBuildNodes.Count})",
                NodeLevel = "AllBuilds",
                Total = allTotal, Passed = allPassed, Failed = allFailed, NotExecuted = allNotExe,
                PassRate = allRate, PassRateColor = GetRateColor(allRate),
                IndentLevel = 0, IsExpanded = GetExpandState("AllBuilds", true),
                ShowEmailButton = true, ShowStats = true,
            };
            allNode.SendEmailCommand = new RelayCommand(SendReportForAllBuilds);
            allNode.ToggleExpandCommand = new RelayCommand(() =>
            {
                SetExpandState("AllBuilds", !GetExpandState("AllBuilds", true));
                BuildFlatList();
            });
            FlatResultsList.Add(allNode);

            if (!allNode.IsExpanded) return;
        }

        foreach (var buildNode in LoadedBuildNodes)
        {
            var buildIndent = hasAllBuildsRow ? 1 : 0;
            var buildKey = $"Build|{buildNode.BuildNumber}";
            var buildExpanded = GetExpandState(buildKey, LoadedBuildNodes.Count == 1);

            var buildRow = new ResultsFlatNode
            {
                Name = buildNode.BuildNumber,
                NodeLevel = "Build", BuildNumber = buildNode.BuildNumber,
                Total = buildNode.TotalTests, Passed = buildNode.PassedTests,
                Failed = buildNode.FailedTests, NotExecuted = buildNode.NotExecutedTests,
                PassRate = buildNode.PassRate, PassRateColor = GetRateColor(buildNode.PassRate),
                IndentLevel = buildIndent, IsExpanded = buildExpanded,
                ShowEmailButton = true, ShowStats = true,
            };
            var capturedBuildKey = buildKey;
            var capturedNode = buildNode;
            buildRow.SendEmailCommand = new RelayCommand(() => SendReportForBuild(capturedNode));
            buildRow.ExportHtmlCommand = new RelayCommand(() => ExportBuildToHtml(capturedNode));
            buildRow.ExportCsvCommand = new RelayCommand(() => ExportBuildToCsv(capturedNode));
            buildRow.ToggleExpandCommand = new RelayCommand(() =>
            {
                SetExpandState(capturedBuildKey, !GetExpandState(capturedBuildKey, false));
                BuildFlatList();
            });
            FlatResultsList.Add(buildRow);

            if (!buildExpanded) continue;

            foreach (var uc in buildNode.UseCases)
            {
                var ucKey = $"UC|{buildNode.BuildNumber}|{uc.UseCaseName}";
                var ucExpanded = GetExpandState(ucKey, false);

                var ucRow = new ResultsFlatNode
                {
                    Name = uc.UseCaseName, NodeLevel = "UseCase", BuildNumber = buildNode.BuildNumber,
                    Total = uc.Total, Passed = uc.Passed, Failed = uc.Failed, NotExecuted = uc.NotExecuted,
                    PassRate = uc.PassRate, PassRateColor = GetRateColor(uc.PassRate),
                    IndentLevel = buildIndent + 1, IsExpanded = ucExpanded, ShowStats = true,
                };
                var capturedUcKey = ucKey;
                ucRow.ToggleExpandCommand = new RelayCommand(() =>
                {
                    SetExpandState(capturedUcKey, !GetExpandState(capturedUcKey, false));
                    BuildFlatList();
                });
                FlatResultsList.Add(ucRow);

                if (!ucExpanded) continue;

                foreach (var test in uc.TestResults)
                {
                    FlatResultsList.Add(new ResultsFlatNode
                    {
                        Name = test.TestName, NodeLevel = "TestResult", BuildNumber = buildNode.BuildNumber,
                        Outcome = test.Outcome, IndentLevel = buildIndent + 2, TestResultModel = test,
                        ErrorMessage = TruncateError(test.ErrorMessage),
                        FullError = test.ErrorMessage ?? "", FullStackTrace = test.StackTrace ?? "",
                        ShowStats = false,
                    });
                }
            }
        }
    }

    // ???????????????????????????????????????????????????????????????
    // Private helpers � Detail panel population
    // ???????????????????????????????????????????????????????????????

    private void PopulateTestResultDetail(ResultsFlatNode node, TestResult tr)
    {
        DetailTestName = tr.TestName;
        DetailUseCaseName = tr.UseCaseName;
        DetailBuildNumber = node.BuildNumber ?? "";
        DetailOutcome = tr.Outcome;
        DetailOutcomeColor = tr.Outcome == "Passed" ? "#10B981"
            : tr.Outcome == "Failed" ? "#EF4444" : "#F59E0B";
        DetailDuration = tr.Duration.ToString(@"hh\:mm\:ss\.fff");
        DetailErrorMessage = tr.ErrorMessage ?? "(no error message)";
        DetailStackTrace = tr.StackTrace ?? "(no stack trace)";
        DetailStdOut = tr.StdOut ?? "(no stdout captured)";
        DetailDebugTrace = tr.DebugTrace ?? "(no debug trace)";
        DetailTrxFileName = tr.TrxFileName;

        var buildPath = LoadedBuildNodes
            .FirstOrDefault(b => b.BuildNumber == node.BuildNumber)?.RootPath;
        if (buildPath != null)
        {
            try
            {
                DetailTrxFilePath = Directory.GetFiles(buildPath,
                    $"*{tr.TrxFileName}*.trx", SearchOption.AllDirectories)
                    .FirstOrDefault() ?? "";
            }
            catch (IOException) { DetailTrxFilePath = ""; }
        }
        else
        {
            DetailTrxFilePath = "";
        }

        DetailExecutionSteps.Clear();
        if (tr.ExecutionSteps.Count > 0)
        {
            foreach (var step in tr.ExecutionSteps)
                DetailExecutionSteps.Add(new StepRowVM
                {
                    StepName = step.StepName,
                    Outcome = step.Outcome,
                    Duration = step.Duration,
                    OutcomeIcon = step.Outcome == "Passed" ? "\u2713" : step.Outcome == "Failed" ? "\u2717" : "\u25CB",
                    DurationText = step.Duration.TotalSeconds < 1
                        ? $"{step.Duration.TotalMilliseconds:F0}ms"
                        : step.Duration.ToString(@"mm\:ss"),
                });
            HasExecutionSteps = true;
        }
        else
        {
            HasExecutionSteps = false;
        }
    }

    private void PopulateAggregateDetail(ResultsFlatNode node)
    {
        DetailTestName = node.Name;
        DetailUseCaseName = "";
        DetailBuildNumber = node.BuildNumber ?? "";
        DetailOutcome = $"{node.Passed}/{node.Total} passed";
        DetailOutcomeColor = GetRateColor(node.PassRate ?? 0);
        DetailDuration = "";
        DetailErrorMessage = node.Failed > 0
            ? $"{node.Failed} test(s) failed" : "All tests passed";
        DetailStackTrace = "";
        DetailStdOut = "";
        DetailDebugTrace = "";
        DetailTrxFileName = "";
        DetailTrxFilePath = "";
        DetailExecutionSteps.Clear();
        HasExecutionSteps = false;
    }
}
