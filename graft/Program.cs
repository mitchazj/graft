﻿using System.Diagnostics;
using graft;
using LibGit2Sharp;
using Octokit;
using Sharprompt;
using Spectre.Console;
using YamlDotNet.RepresentationModel;
using Credentials = Octokit.Credentials;

// TODO: implement extras like this
// var root = new RootCommand
// {
//     new Option<string>("--token", "The github token to use"),
//     new Option<string>("--branch", "The branch to start the train from"),
//     new Option<bool>("--dry-run", "Don't actually do anything, just print what would happen"),
//     new Option<bool>("--verbose", "Print more information"),
// }; 

// Determine the current working directory
var rootPath = Environment.CurrentDirectory;

// TODO: technically there's an error case to consider here I suppose
// Go up until we find a .git folder
while (!Directory.Exists(Path.Combine(rootPath, ".git")))
{
    rootPath = Path.GetDirectoryName(rootPath);
}

// Open the repository
var repo = new LibGit2Sharp.Repository(rootPath);
var currentBranch = repo.Head.FriendlyName;

// Check whether the .pr-train.yml file exists
if (!File.Exists(rootPath + "/.pr-train.yml"))
{
    AnsiConsole.MarkupLine("[red]Error:[/] No .pr-train.yml file found in the repository root");
    return;
}

// Read the .pr-train.yml file
var yaml = new YamlStream();
var ymlFilePath = rootPath + "/.pr-train.yml";
using (var reader = new StreamReader(ymlFilePath))
{
    yaml.Load(reader);
}

// Check whether USER_HOME/.branch/token exists
if (!File.Exists(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + "/.graft/token"))
{
    AnsiConsole.MarkupLine("[red]Error:[/] No github token found in ~/.graft/token");
    return;
}

// Read the github token
var token = File.ReadAllText(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + "/.graft/token")
    .ReplaceLineEndings("");

// Create the github client
GitHubClient client;
try
{
    client = new GitHubClient(new ProductHeaderValue("graft")) { Credentials = new Credentials(token) };
}
catch
{
    AnsiConsole.MarkupLine("[red]Error:[/] Failed to authenticate with github");
    return;
}

// Get the base branch
string baseBranch;
string baseMergeBranch;
try
{
    baseBranch = ((YamlMappingNode)yaml.Documents[0].RootNode["prs"])["main-branch-name"].ToString();
    baseMergeBranch = ((YamlMappingNode)yaml.Documents[0].RootNode["prs"])["branch-into"].ToString();
}
catch
{
    AnsiConsole.MarkupLine("[red]Error:[/] No base branch specified in .pr-train.yml");
    return;
}

var trains = (YamlMappingNode)yaml.Documents[0].RootNode["trains"];

var allBranches = trains.Children.Values.SelectMany(x => ((YamlSequenceNode)x).Children)
    .Select<YamlNode, GraftBranch>(x => x is YamlScalarNode
        ? new GraftBranch(x.ToString())
        // todo: better way to interpret the merged flag bc this sucks lol
        : new GraftBranch(((YamlMappingNode)x).Children.First().Key.ToString(), true)).ToList();

if (!allBranches.Select(x => x.Name).Contains(currentBranch))
{
    AnsiConsole.MarkupLine("[red]Error:[/] The current branch is not a PR branch");
    return;
}

// Select the train that the current branch is on
var train = trains.Children.First(x =>
    ((YamlSequenceNode)x.Value).Children
    .Select<YamlNode, string>(x =>
        x is YamlScalarNode ? x.ToString() : ((YamlMappingNode)x).Children.First().Key.ToString())
    .Contains(currentBranch));

var trainName = train.Key.ToString();

var branches = ((YamlSequenceNode)train.Value).Children
    .Select<YamlNode, GraftBranch>(x => x is YamlScalarNode
        ? new GraftBranch(x.ToString())
        // todo: better way to interpret the merged flag bc this sucks lol
        : new GraftBranch(((YamlMappingNode)x).Children.First().Key.ToString(), true)).ToList();

branches.Add(new GraftBranch(baseBranch));

// Now we construct the train on the console
AnsiConsole.MarkupLine($"[gray]{trainName}[/]");
AnsiConsole.Status()
    .Start($"Building graft train...", ctx =>
    {
        FetchBranches();
        Console.WriteLine();
        AnsiConsole.MarkupLine($"- [blue]{baseBranch}[/] [gray]({GetBaseBranchStatus()})[/]");

        // Now we construct the train on the console
        foreach (var branch in branches)
        {
            if (branch.Name == baseBranch) continue;

            if (branch.IsMerged)
            {
                AnsiConsole.MarkupLine($"- [gray]{branch.Name}[/] [gray](merged)[/]");
                continue;
            }

            var name = branch.Name == currentBranch
                ? $"[green]{branch.Name}[/]"
                : branch.Name;

            AnsiConsole.MarkupLine($"- {name} {GetBranchStatus(branch.Name)} {GetRemoteBranchStatus(branch.Name)}");
        }
    });

Console.WriteLine();

var userName = repo.Config.Get<string>("user.name");
var userEmail = repo.Config.Get<string>("user.email");
if (userName == null || userEmail == null)
{
    AnsiConsole.MarkupLine("[red]Error:[/] Your user.name and user.email are not set in git, please fix manually.");
    return;
}

// TODO: figure out how to do failsafe recovery? Eg, if it exits in the middle of a graft, how to safely resume?
// what things would we need to consider?

bool IsRepoDirty()
{
    bool isDirty = false;

    try
    {
        ProcessStartInfo startInfo = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = "status --porcelain",
            WorkingDirectory = rootPath,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using (Process process = new Process())
        {
            process.StartInfo = startInfo;
            process.Start();

            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            isDirty = !string.IsNullOrWhiteSpace(output);
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine("An error occurred while executing Git command: " + ex.Message);
    }

    return isDirty;
}

bool IsMergeConflict(string mergeOutput)
{
    // Check if the merge output contains conflict markers
    return mergeOutput.Contains("Automatic merge failed; fix conflicts");
    // return mergeOutput.Contains("<<<<<<<") || mergeOutput.Contains("=======") || mergeOutput.Contains(">>>>>>>");
}

GraftMergeResult MergeBranch(string sourceBranch)
{
    string output = string.Empty;

    try
    {
        ProcessStartInfo startInfo = new ProcessStartInfo
        {
            FileName = "git",
            // Arguments = $"merge --no-commit {sourceBranch}",
            Arguments = $"merge {sourceBranch}",
            WorkingDirectory = rootPath,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using (Process process = new Process())
        {
            process.StartInfo = startInfo;
            process.Start();

            output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine("An error occurred while executing Git command: " + ex.Message);
        return GraftMergeResult.Error;
    }

    bool isConflict = IsMergeConflict(output);

    if (isConflict)
    {
        return GraftMergeResult.Conflicts;
    }
    else
    {
        // Console.WriteLine("Attempting to complete merge...");
        // Thread.Sleep(300);

        // try
        // {
        //     ProcessStartInfo commitInfo = new ProcessStartInfo
        //     {
        //         FileName = "git",
        //         Arguments = "add .",
        //         WorkingDirectory = rootPath,
        //         RedirectStandardOutput = true,
        //         UseShellExecute = false,
        //         CreateNoWindow = true
        //     };

        //     using (Process commitProcess = new Process())
        //     {
        //         commitProcess.StartInfo = commitInfo;
        //         commitProcess.Start();
        //         commitProcess.WaitForExit();
        //     }
        // }
        // catch (Exception ex)
        // {
        //     Console.WriteLine("An error occurred while adding files to the merge: " + ex.Message);
        //     return GraftMergeResult.Error;
        // }

        // Console.WriteLine($"Finishing merge... `commit -m 'Merging in {sourceBranch}'`");
        // Thread.Sleep(300);

        // try
        // {
        //     ProcessStartInfo commitInfo = new ProcessStartInfo
        //     {
        //         FileName = "git",
        //         Arguments = $"commit -m 'Merging in {sourceBranch}'",
        //         WorkingDirectory = rootPath,
        //         RedirectStandardOutput = true,
        //         UseShellExecute = false,
        //         CreateNoWindow = true
        //     };

        //     using (Process commitProcess = new Process())
        //     {
        //         commitProcess.StartInfo = commitInfo;
        //         commitProcess.Start();
        //         commitProcess.WaitForExit();
        //     }
        // }
        // catch (Exception ex)
        // {
        //     Console.WriteLine("An error occurred while committing the merge: " + ex.Message);
        //     return GraftMergeResult.Error;
        // }

        return GraftMergeResult.Success;
    }
}

string Checkout(string branchName)
{
    string output = string.Empty;

    try
    {
        ProcessStartInfo startInfo = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = $"checkout {branchName}",
            WorkingDirectory = rootPath,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using (Process process = new Process())
        {
            process.StartInfo = startInfo;
            process.Start();

            output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine("An error occurred while executing Git command: " + ex.Message);
    }

    return output;
}

if (IsRepoDirty())
{
    AnsiConsole.MarkupLine(
        "[red]Error:[/] Your current repository is in a dirty state, please fix this before continuning.");
    return;
}

// TODO: doing this First() lookup everywhere will get very slow eventually, fix
if (branches.First(x => x.Name == baseBranch).BehindOriginBy > 0)
{
    // Merge origin/base into local/base
    var baseBranchRepo = repo.Branches[baseBranch];
    var baseBranchRepoUpstream = repo.Branches[baseBranch].TrackedBranch;

    Checkout(baseBranchRepo.FriendlyName);

//    var mergeResult = repo.Merge(baseBranchRepoUpstream.Tip,
//        new LibGit2Sharp.Signature(userName.Value, userEmail.Value, DateTimeOffset.Now));

    var mergeResult = MergeBranch(baseBranchRepoUpstream.FriendlyName);
    if (mergeResult == GraftMergeResult.Conflicts)
    {
        AnsiConsole.MarkupLine(
            $"[yellow]Warning:[/] Encountered conflicts updating {baseBranch}, please resolve them in a seperate terminal before continuing.");
        Console.WriteLine();
        if (!Prompt.Confirm("Continue?")) return;
        Thread.Sleep(100);
        if (IsRepoDirty())
        {
            AnsiConsole.MarkupLine(
                $"[yellow]Warning:[/] Repo is diry, please fix before continuing.");
            if (!Prompt.Confirm("Continue?")) return;
        }
    }

    CompareToRemote(baseBranch);
    AnsiConsole.MarkupLine($"[green]Merged:[/] {baseBranchRepoUpstream.FriendlyName} into {baseBranch}");
}
else
{
    AnsiConsole.MarkupLine($"[gray]{baseBranch} is already up-to-date.[/]");
}

for (var i = 0; i < branches.Count; ++i)
{
    var branch = branches[i];

    if (branch.Name == baseBranch) continue;

    if (branch.IsMerged)
    {
        // AnsiConsole.MarkupLine($"[gray]Skipped: {branch.Name} because it is merged");
        continue;
    }

    if (branch.BehindOriginBy > 0)
    {
        var branchRepo = repo.Branches[branch.Name];
        var branchRepoUpstream = branchRepo.TrackedBranch;
        Checkout(branchRepo.FriendlyName);
//        var mergeResult = repo.Merge(branchRepoUpstream.Tip,
//            new LibGit2Sharp.Signature(userName.Value, userEmail.Value, DateTimeOffset.Now));
        var mergeResult = MergeBranch(branchRepoUpstream.FriendlyName);
        if (mergeResult == GraftMergeResult.Conflicts)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]Warning:[/] Encountered conflicts updating {branch.Name}, please resolve them in a seperate terminal before continuing.");
            Console.WriteLine();
            if (!Prompt.Confirm("Continue?")) return;
            Thread.Sleep(100);
            if (IsRepoDirty())
            {
                AnsiConsole.MarkupLine(
                    $"[yellow]Warning:[/] Repo is diry, please fix before continuing.");
                if (!Prompt.Confirm("Continue?")) return;
            }
            // TODO: double-check that the above is correct and good
        }

        CompareToRemote(baseBranch);
    }
}

AnsiConsole.MarkupLine("[gray]train branches are up-to-date.[/]");
Console.WriteLine();

var remote = repo.Network.Remotes["origin"];
string owner;
string repoName;
try
{
    var url = new Uri(remote.Url);
    owner = url.Segments[1].TrimEnd('/'); // Extract the owner/organization from the URL
    repoName = url.Segments[2].TrimEnd('/'); // Extract the repository name from the URL
}
catch
{
    try
    {
        var urlParts = remote.Url.Split(':');
        var ownerAndRepo = urlParts[1].Split('/');
        owner = ownerAndRepo[0];
        repoName = ownerAndRepo[1].Replace(".git", "");
    }
    catch
    {
        AnsiConsole.MarkupLine(
            "[red]Error:[/] failed to parse the git origin to determine the owner & repo name on origin.");
        return;
    }
}

AnsiConsole.Status()
    .Start("Checking PRs...", ctx =>
    {
        for (var i = 0; i < branches.Count; ++i)
        {
            var branch = branches[i];
            if (branch.Name == baseBranch) continue;
            // if (branch.IsMerged) continue;

            var searchForPRs = new PullRequestRequest()
            {
                Head = owner + ":" + branch.Name,
                State = ItemStateFilter.All
            };

            var pullRequestsTask = client.PullRequest.GetAllForRepository(owner, repoName, searchForPRs);
            var pullRequests = pullRequestsTask.Result;

            if (pullRequests.Count == 0) continue;

            branch.PullRequests = pullRequests.ToList();
        }
    });

foreach (var branch in branches)
{
    if (branch.Name == baseBranch) continue;
    if (branch.IsMerged) continue;

    if (!branch.PullRequests.Exists(x => x.ClosedAt == null))
    {
        var markMerged = $"Mark {branch.Name} as merged";
        var createNewPr = $"Create a new PR for {branch.Name}";
        // TODO: add "Remove this branch from the train entirely"

        var choice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title($"Every PR attached to {branch.Name} has been closed on origin. Would you like to")
                .AddChoices(new[]
                {
                    markMerged,
                    createNewPr
                }));

        if (choice == markMerged)
        {
            branch.StoreMergeStatus(true, ymlFilePath, baseBranch);
            AnsiConsole.MarkupLine($"[gray]Marked {branch.Name} as merged[/]");
        }
        else
        {
            AnsiConsole.MarkupLine($"[gray]We'll create a new PR for {branch.Name}[/]");
        }
    }
}

// Graft master into the first unmerged branch in train
GraftBranch firstBranchNotMerged;
try
{
    firstBranchNotMerged = branches.SkipWhile(x => x.IsMerged)
        .SkipWhile(x => x.Name == baseBranch)
        .FirstOrDefault();

    if (firstBranchNotMerged == null) throw new Exception();
}
catch
{
    AnsiConsole.MarkupLine("[red]Couldn't locate an unmerged branch in train. Exiting.[/]");
    return;
}

var shouldUpdateOnMaster = Prompt.Confirm($"Update this train on {baseBranch}?");
if (shouldUpdateOnMaster)
{
    bool result = Graft(baseBranch, firstBranchNotMerged.Name);
    if (!result)
    {
        AnsiConsole.MarkupLine("[red]Failed to update on master automatically[/]");
        return; // TODO: is this correct/desirable?
    }

    AnsiConsole.MarkupLine("[gray]Updated on master[/]");
}

// If we grafted baseBranch in (or if the first branch already had changes) push those changes to origin
if (shouldUpdateOnMaster || firstBranchNotMerged.AheadOfOriginBy > 0)
{
    if (repo.Head.FriendlyName != firstBranchNotMerged.Name)
    {
        Checkout(repo.Branches[firstBranchNotMerged.Name].FriendlyName);
    }

    Thread.Sleep(100);
    PushCurrentBranch();
    AnsiConsole.MarkupLine(
        $"[gray]Pushed {firstBranchNotMerged.Name} to {repo.Branches[firstBranchNotMerged.Name].TrackedBranch.FriendlyName}[/]");

    Thread.Sleep(100);
}

AnsiConsole.MarkupLine("[gray]grafting...[/]");

// Now that we have handled the base case (optionally grafting baseBranch into firstBranchNotMergeed)
// we continue the work.
for (var i = 0; i < branches.Count; ++i)
{
    start:
    var branch = branches[i];

    if (branch.IsMerged) continue;
    if (branch.Name == baseBranch) continue;
    if (i + 1 < branches.Count && branches[i + 1].Name == baseBranch) continue;

    var nextBranch = branches.SkipWhile(x => x.Name != branch.Name)
        .Skip(1)
        .SkipWhile(x => x.IsMerged)
        .FirstOrDefault();

    if (nextBranch.Name == baseBranch)
    {
        Console.WriteLine($"Nothing to graft {branch.Name} into");
        continue;
    }

    // We need to recompare at each iteration because we are modifying.
    CompareBranches(branch.Name, nextBranch.Name);

    if (branch.AheadOfNextBranchBy > 0)
    {
        bool result = Graft(branch.Name, nextBranch.Name);
        if (!result)
        {
            goto start;
        }
    }

    if (nextBranch.AheadOfOriginBy > 0 || branch.AheadOfNextBranchBy > 0)
    {
        if (repo.Head.FriendlyName != nextBranch.Name)
        {
            Checkout(repo.Branches[nextBranch.Name].FriendlyName);
        }

        Thread.Sleep(100);
        PushCurrentBranch();
        AnsiConsole.MarkupLine(
            $"[gray]Pushed {nextBranch.Name} to {repo.Branches[nextBranch.Name].TrackedBranch.FriendlyName}[/]");
    }

    Thread.Sleep(100);
}

var EXCESSIVE_DEBUG = false;

AnsiConsole.Status()
    .Start("Updating PR train...", ctx =>
    {
        var previousBranch = "";
        for (var i = branches.Count - 2; i >= 0; --i)
        {
            var branch = branches[i];
            if (EXCESSIVE_DEBUG) Console.WriteLine($"Looking at {branch.Name}");

            if (branch.Name == baseBranch) continue;

            if (EXCESSIVE_DEBUG)
                Console.WriteLine($"Found {branch.PullRequests.Count} pull requests for {branch.Name}");
            foreach (var pr in branch.PullRequests)
            {
                PullRequestUpdate update = new PullRequestUpdate();
                update.Body = SubstituteTrainTable(pr.Body, GenerateTrainTable(branch.Name, branches));
                client.PullRequest.Update(owner, repoName, pr.Number, update).Wait();
                AnsiConsole.MarkupLine($"[gray]Updated pr #{pr.Number} for {branch.Name}[/]");
            }

            if (previousBranch == "")
            {
                if (EXCESSIVE_DEBUG) Console.WriteLine($"Set previous branch as {branch.Name}");
                if (!branch.IsMerged) previousBranch = branch.Name;
                // TODO: I think I need to do something here
                continue;
            }

            var previousBranchBr = branches.First(x => x.Name == previousBranch);

            // Find the first pr that is open (ie doesn't have a "closed at" date)
            var openPr = previousBranchBr.PullRequests.Find(x => x.ClosedAt == null);
            if (openPr == null && !branch.IsMerged)
            {
                try
                {
                    var pullRequest =
                        new NewPullRequest($"Merge {previousBranch} into {branch.Name}", previousBranch, branch.Name)
                        {
                            Body = GenerateTrainTable(previousBranch, branches)
                        };
                    var createdPullRequestTask = client.PullRequest.Create(owner, repoName, pullRequest);
                    createdPullRequestTask.Wait();
                    AnsiConsole.MarkupLine($"[gray]Created a pr for {previousBranch}[/]");
                }
                catch
                {
                    AnsiConsole.MarkupLine(
                        $"[red]Coultn't create a pr for {previousBranch}, please check that origin exists and that there are sufficient changes[/]");
                }
            }

            if (branch.IsMerged) continue;

            previousBranch = branch.Name;
        }

        // Handle merging the first branch into baseMergeBranch
        if (previousBranch != "")
        {
            var branch = branches.First(x => x.Name == previousBranch);
            if (EXCESSIVE_DEBUG) Console.WriteLine($"Looking at {branch.Name} for merging into master");
            if (EXCESSIVE_DEBUG) Console.WriteLine($"It has {branch.PullRequests.Count} pull requests");

            foreach (var pr in branch.PullRequests)
            {
                PullRequestUpdate update = new PullRequestUpdate();
                update.Body = SubstituteTrainTable(pr.Body, GenerateTrainTable(previousBranch, branches));
                client.PullRequest.Update(owner, repoName, pr.Number, update).Wait();
                AnsiConsole.MarkupLine($"[gray]Updated pr #{pr.Number} for {previousBranch}[/]");
            }

            var openPr = branch.PullRequests.Find(x => x.ClosedAt == null);
            if (openPr == null && !branch.IsMerged)
            {
                try
                {
                    var pullRequest =
                        new NewPullRequest($"Merge {branch.Name} into {baseMergeBranch}", branch.Name, baseMergeBranch)
                        {
                            Body = GenerateTrainTable(branch.Name, branches)
                        };
                    var createdPullRequestTask = client.PullRequest.Create(owner, repoName, pullRequest);
                    createdPullRequestTask.Wait();
                    AnsiConsole.MarkupLine($"[gray]Created a pr for {branch.Name}[/]");
                }
                catch
                {
                    AnsiConsole.MarkupLine(
                        $"[red]Coultn't create a pr for {branch.Name}, please check that origin exists and that there are sufficient changes[/]");
                }
            }
        }
    });

Console.WriteLine();

if (repo.Head.FriendlyName != currentBranch)
{
    Console.WriteLine($"Taking you back to {currentBranch}...");
    Checkout(currentBranch);
}

Console.WriteLine("All done!");

///////////////////////////////////////////////////////////////////////////////

bool Graft(string branchName, string nextBranchName)
{
    var branchRepo = repo.Branches[branchName];
    var nextBranchRepo = repo.Branches[nextBranchName];

    try
    {
        Checkout(nextBranchRepo.FriendlyName);

//        var mergeResult = repo.Merge(branchRepo.Tip,
//            new LibGit2Sharp.Signature(userName.Value, userEmail.Value, DateTimeOffset.Now));

        var mergeResult = MergeBranch(branchRepo.FriendlyName);
        if (mergeResult == GraftMergeResult.Conflicts)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]Warning:[/] Encountered conflicts grafting {branchName}, please resolve them in a seperate terminal before continuing.");
            Console.WriteLine();
            if (!Prompt.Confirm("Continue?"))
            {
                Environment.Exit(1);

                // return true even in failure case here so that we exit faster
                return true;
            }

            Thread.Sleep(100);
            if (IsRepoDirty())
            {
                AnsiConsole.MarkupLine(
                    $"[yellow]Warning:[/] Repo is diry, please fix before continuing.");
                if (!Prompt.Confirm("Continue?"))

                {
                    Environment.Exit(1);

                    // return true even in failure case here so that we exit faster
                    return true;
                }
            }
        }

        AnsiConsole.MarkupLine($"[green]Grafted {branchName} unto {nextBranchName}.[/]");
        Thread.Sleep(100);
    }
    catch (LibGit2Sharp.LockedFileException)
    {
        AnsiConsole.MarkupLine(
            $"[yellow]Warning: Encountered locked file exception while grafting {branchName} unto {nextBranchName}. Retrying...[/]");

        // Wait for safety
        Thread.Sleep(1000);

        // Undo any weirdness that might have occurred
        repo.Reset(ResetMode.Hard, repo.Head.Tip);

        return false;
    }

    return true;
}

string GetBranchStatus(string branchName)
{
    try
    {
        var nextBranch = branches.SkipWhile(x => x.Name != branchName)
            .Skip(1)
            .SkipWhile(x => x.IsMerged)
            .FirstOrDefault();

        if (nextBranch.Name == baseBranch)
        {
            // The base branch is always the last branch on the train
            throw new KnownException("end of train");
        }

        var (ahead, _) = CompareBranches(branchName, nextBranch.Name);
        return ahead == 0
            ? $"[gray]({ahead} commits need grafting)[/]"
            : $"[yellow]({ahead} commits need grafting)[/]";
    }
    catch
    {
        // TODO: make this catch more specific to avoid swallowing unintentional errors
        return "[gray](end of train)[/]";
    }
}

string GetRemoteBranchStatus(string branchName)
{
    try
    {
        var (ahead, behind) = CompareToRemote(branchName);

        if (behind == -1)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {branchName} has diverged from origin. Please fix this manually.");
            Environment.Exit(1);
        }

        var behindColor = behind == 0 ? "gray" : "blue";
        var aheadColor = ahead == 0 ? "gray" : "blue";
        return
            $"[gray]([/][{behindColor}]origin: {behind} un-pulled commits[/][gray],[/] [{aheadColor}]{ahead} to push[/][gray])[/]";
    }
    catch (KnownException)
    {
        return "[blue](no origin branch)[/]";
    }
}

string GetBaseBranchStatus()
{
    try
    {
        var (ahead, behind) = CompareToRemote(baseBranch);

        if (ahead > 0)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] The base branch is ahead of origin. Please fix this manually.");
            Environment.Exit(1);
        }

        if (behind == -1)
        {
            AnsiConsole.MarkupLine(
                "[red]Error:[/] The base branch has diverged from origin. Please fix this manually.");
            Environment.Exit(1);
        }

        return $"{behind} commits behind";
    }
    catch (KnownException)
    {
        AnsiConsole.MarkupLine(
            "[red]Error:[/] The base branch does not have a tracking branch. Please fix this manually.");
        Environment.Exit(1);
        return "";
    }
}

void PushCurrentBranch()
{
    // Use a call to the git cli instead of libgit2sharp, since libgit2sharp
    // doesn't play nicely with SSH credentials.

    // This approaches lets us use the user's system git + ssh-agent
    // todo: does this surface roo / okta 2fa requests correctly?

    var psi = new ProcessStartInfo
    {
        FileName = "git",
        Arguments = "push",
        WorkingDirectory = rootPath,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true,
        UseShellExecute = false
    };
    var process = Process.Start(psi);

    process.WaitForExit();

    if (process.ExitCode == 0) return;

    AnsiConsole.MarkupLine("[red]Error:[/] Failed to push to origin");
    AnsiConsole.WriteLine("");
    AnsiConsole.WriteLine(process.StandardError.ReadToEnd());
    Environment.Exit(1);
}

void FetchBranches()
{
    // Use a call to the git cli instead of libgit2sharp, since libgit2sharp
    // doesn't play nicely with SSH credentials.

    // This approaches lets us use the user's system git + ssh-agent
    // todo: does this surface roo / okta 2fa requests correctly?

    var psi = new ProcessStartInfo
    {
        FileName = "git",
        Arguments = "fetch",
        WorkingDirectory = rootPath,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true,
        UseShellExecute = false
    };
    var process = Process.Start(psi);

    process.WaitForExit();

    if (process.ExitCode == 0) return;

    AnsiConsole.MarkupLine("[red]Error:[/] Failed to fetch branches from origin");
    AnsiConsole.WriteLine("");
    AnsiConsole.WriteLine(process.StandardError.ReadToEnd());
    Environment.Exit(1);
}

(int? ahead, int? behind) CompareBranches(string branchName, string compareBranchName)
{
    var branch = repo.Branches[branchName];
    if (branch == null)
    {
        AnsiConsole.MarkupLine($"[red]Error:[/] No branch {branchName} exists, please repair your .pr-train.yml file");
        Environment.Exit(1);
        return (0, 0);
    }

    var compareBranch = repo.Branches[compareBranchName];
    if (compareBranch == null)
    {
        AnsiConsole.MarkupLine(
            $"[red]Error:[/] No branch {compareBranchName} exists, please repair your .pr-train.yml file");
        Environment.Exit(1);
        return (0, 0);
    }

    var ahead = repo.ObjectDatabase.CalculateHistoryDivergence(branch.Tip, compareBranch.Tip).AheadBy;
    var behind = repo.ObjectDatabase.CalculateHistoryDivergence(branch.Tip, compareBranch.Tip).BehindBy;

    if (ahead != null && behind != null)
    {
        branches.First(x => x.Name == branchName).AheadOfNextBranchBy = (int)ahead;
        branches.First(x => x.Name == branchName).BehindNextBranchBy = (int)behind;
        return (ahead, behind);
    }

    AnsiConsole.MarkupLine(
        $"[red]Error:[/] {branchName} has diverged from {compareBranchName}. Please fix this manually.");
    Environment.Exit(1);
    return (-1, -1);
}

(int? ahead, int? behind) CompareToRemote(string branchName)
{
    var branch = repo.Branches[branchName];
    if (branch == null)
    {
        AnsiConsole.MarkupLine($"[red]Error:[/] No branch {branchName} exists, please repair your .pr-train.yml file");
        Environment.Exit(1);
        return (0, 0);
    }

    var trackingBranch = branch.TrackedBranch;
    if (trackingBranch == null)
    {
        branches.First(x => x.Name == branchName).HasOrigin = false;
        throw new KnownException($"No tracked branch for {branchName}");
    }

    var ahead = repo.ObjectDatabase.CalculateHistoryDivergence(branch.Tip, trackingBranch.Tip).AheadBy;
    var behind = repo.ObjectDatabase.CalculateHistoryDivergence(branch.Tip, trackingBranch.Tip).BehindBy;

    if (ahead == null || behind == null)
    {
        // TODO: check that this is implemented correctly
        branches.First(x => x.Name == branchName).HasDivergedFromOrigin = true;
        return (-1, -1);
    }

    branches.First(x => x.Name == branchName).AheadOfOriginBy = (int)ahead;
    branches.First(x => x.Name == branchName).BehindOriginBy = (int)behind;

    return (ahead, behind);
}

string SubstituteTrainTable(string existingBody, string newTable)
{
    var openToc = "<pr-train-toc>";
    var closeToc = "</pr-train-toc>";
    var locationOfStart = existingBody.IndexOf(openToc);
    var locationOfEnd = existingBody.LastIndexOf(closeToc);
    if (locationOfStart == -1 || locationOfEnd == -1)
    {
        return newTable + "\n \n" + existingBody;
    }

    return existingBody.Substring(0, locationOfStart) + newTable +
           existingBody.Substring(locationOfEnd + closeToc.Length);
}

string GenerateTrainTable(string thisBranch, List<GraftBranch> branches)
{
    //
    // <pr-train-toc>
    // |     | PR      | Description                                                 |
    // | --- | ------- | ----------------------------------------------------------- |
    // | 👉  | #402375 | Description here                                            |
    // |     | #403234 | Description here                                            |
    // |     | #403260 | Page/Component - Desc - Ticket #                            |
    // </pr-train-toc>
    //

    string table = "<pr-train-toc>\r\n \r\n";
    table += "|   | PR | Status | Description |\n";
    table += "| - | -- | ------ | ----------- |\n";
    foreach (var branch in branches)
    {
        var isBranch = branch.Name == thisBranch;
        var isBranchAppendable = isBranch ? "👉" : " ";
        List<PullRequest> prs = new List<PullRequest>();
        prs.AddRange(branch.PullRequests);
        prs.Sort((a, b) => a.ClosedAt == null
            ? (b.ClosedAt == null ? 0 : 1)
            : (b.ClosedAt == null
                ? 1
                : 0));
        foreach (var pr in prs)
        {
            var title = pr.Title.Replace("[merged]", "").Trim();
            var isMergedAppendable = pr.ClosedAt != null ? "`merged`" : "`open`";
            if (!title.Contains("(ignore)"))
            {
                table += $"| {isBranchAppendable} | #{pr.Number} | {isMergedAppendable} | {title} |\n";
            }
        }
    }

    table += "\n \r\n</pr-train-toc>";
    return table;
}

public class KnownException : Exception
{
    public KnownException(string s)
    {
        // TODO: do something with the string?
    }
}
