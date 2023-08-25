using System.Diagnostics;
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
var token = File.ReadAllText(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + "/.graft/token");

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
try
{
    baseBranch = ((YamlMappingNode)yaml.Documents[0].RootNode["prs"])["main-branch-name"].ToString();
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

// TODO: exit if the repo is dirty / uncommitted changes
// Get base branch status (as above)
//   fetch origin/base DONE
//   - make note of how many commits it is behind DONE
//     - if it is ahead, this is a fail state. tell the user to resolve manually DONE
// Get branch status (as above)
//   for each branch
//     fetch origin/branch DONE
//     - is it a merged branch? DONE
//         - if yes, skip it, but make note (so that the PR tables can be updated) DONE
//     - check that there *is* an origin branch DONE
//         - if there is not, make note of this for later (so we can create one) DONE
//     - check if there are un-pulled commits on the origin DONE
//         - if there are, make note of this for later (so we can pull them) DONE
//     - check if there is a divergent history on the origin DONE
//         - if there is, fail and notify the user to resolve manually DONE
//     - check how many commits are on this branch that are not on the next branch DONE
//
// merge origin/base into local/base (should fast-forward) DONE
// for each branch (skipping those marked as merged)
//   merge origin/branch into local/branch (should fast-forward, but if not, pause for conflict resolution)
// merge local/base into local/branch1 (or next, if branch1 is merged)
// then merge local/branch1 into local/branch2, local/branch2 into local/branch3, etc (skipping merged branches)
// for each branch (skipping those marked as merged)
//   push local/branch to origin/branch, creating origin/branch if it does not exist
// for each branch
//   if there is a PR for the branch
//     update the table
//   else
//     create a PR for the branch with updated table

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

if (repo.RetrieveStatus().IsDirty)
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

    Commands.Checkout(repo, baseBranchRepo);

    var mergeResult = repo.Merge(baseBranchRepoUpstream.Tip,
        new LibGit2Sharp.Signature(userName.Value, userEmail.Value, DateTimeOffset.Now));

    if (mergeResult.Status == MergeStatus.Conflicts)
    {
        AnsiConsole.MarkupLine(
            $"[yellow]Warning:[/] Encountered conflicts updating {baseBranch}, please resolve them in a seperate terminal before continuing.");
        Console.WriteLine();
        if (!Prompt.Confirm("Continue?")) return;
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
        Commands.Checkout(repo, branchRepo);
        var mergeResult = repo.Merge(branchRepoUpstream.Tip,
            new LibGit2Sharp.Signature(userName.Value, userEmail.Value, DateTimeOffset.Now));
        if (mergeResult.Status == MergeStatus.Conflicts)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]Warning:[/] Encountered conflicts updating {branch.Name}, please resolve them in a seperate terminal before continuing.");
            Console.WriteLine();
            if (!Prompt.Confirm("Continue?")) return;
        }

        CompareToRemote(baseBranch);
    }
}

AnsiConsole.MarkupLine("[gray]train branches are up-to-date.[/]");
Console.WriteLine();
AnsiConsole.MarkupLine("[gray]grafting...[/]");

for (var i = 0; i < branches.Count; ++i)
{
    var branch = branches[i];

    if (branch.IsMerged) continue;
    if (branch.Name == baseBranch) continue;
    if (i + 1 < branches.Count && branches[i + 1].Name == baseBranch) continue;

    var nextBranch = branches.SkipWhile(x => x.Name != branch.Name)
        .SkipWhile(x => x.IsMerged)
        .Skip(1)
        .FirstOrDefault();

    // We need to recompare at each iteration because we are modifying.
    CompareBranches(branch.Name, nextBranch.Name);

    if (branch.AheadOfNextBranchBy > 0)
    {
        var branchRepo = repo.Branches[branch.Name];
        var nextBranchRepo = repo.Branches[nextBranch.Name];

        Commands.Checkout(repo, nextBranchRepo);

        var mergeResult = repo.Merge(branchRepo.Tip,
            new LibGit2Sharp.Signature(userName.Value, userEmail.Value, DateTimeOffset.Now));

        if (mergeResult.Status == MergeStatus.Conflicts)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]Warning:[/] Encountered conflicts grafting {branch.Name}, please resolve them in a seperate terminal before continuing.");
            Console.WriteLine();
            if (!Prompt.Confirm("Continue?")) return;
        }

        AnsiConsole.MarkupLine($"[green]Grafted {branch.Name} unto {nextBranch.Name}.[/]");
    }
}

Console.WriteLine();
Console.WriteLine("All done!");

// Console.WriteLine();
// var city = Prompt.Select($"The PR attached to {currentBranch} has been closed on origin. Would you like to", new[]
// {
//     "Mark this branch as merged",
//     "Create a new PR for this branch",
//     "Remove this branch from the train entirely"
// });
// Console.WriteLine($"Hello, {city}!");


// Mark the "mitchazj-branch-three" branch as merged
// var cb = branches.First(x => x.Name == "mitchazj-branch-three");
// cb.StoreMergeStatus(!cb.IsMerged, ymlFilePath, baseBranch);

string GetBranchStatus(string branchName)
{
    try
    {
        var nextBranch = branches.SkipWhile(x => x.Name != branchName)
            .SkipWhile(x => x.IsMerged)
            .Skip(1)
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
        var (_, behind) = CompareToRemote(branchName);

        if (behind == -1)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {branchName} has diverged from origin. Please fix this manually.");
            Environment.Exit(1);
        }

        return behind == 0
            ? $"[gray](origin: {behind} un-pulled commits)[/]"
            : $"[blue](origin: {behind} un-pulled commits)[/]";
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

public class KnownException : Exception
{
    public KnownException(string s)
    {
        // TODO: do something with the string?
    }
}
