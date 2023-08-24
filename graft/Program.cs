// See https://aka.ms/new-console-template for more information

using graft;
using Octokit;
using Sharprompt;
using Spectre.Console;
using YamlDotNet.RepresentationModel;

// var root = new RootCommand
// {
//     new Option<string>("--token", "The github token to use"),
//     new Option<string>("--branch", "The branch to start the train from"),
//     new Option<bool>("--dry-run", "Don't actually do anything, just print what would happen"),
//     new Option<bool>("--verbose", "Print more information"),
// }; 

// Determine the current working directory
var rootPath = Environment.CurrentDirectory;

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
var client = new GitHubClient(new ProductHeaderValue("graft")) { Credentials = new Credentials(token) };

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

// Next we need to check whether the current branch is a PR branch.
// We can determine this by seeing if the branch name appears in .pr-train.yml which has structure:
// prs:
//   base: master
//   # config
// trains:
//   some train name:
//     - branch1
//     - branch2:
//         dead: true
//     - branch3
//   some other train name:
//     - cool-changes
//     - more-cool-changes

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

// Now we construct the train on the console
AnsiConsole.MarkupLine($"[gray]{trainName}[/]");
AnsiConsole.Status()
    .Start($"Fetching origin/{baseBranch}...", ctx =>
    {
        Thread.Sleep(3000);
        AnsiConsole.MarkupLine($"- [blue]{baseBranch}[/] [gray]({GetBaseBranchStatus()})[/]");
    });

// Synchronous
AnsiConsole.Status()
    .Start("Thinking...", ctx =>
    {
        ctx.Spinner(Spinner.Known.Aesthetic);
        ctx.SpinnerStyle(Style.Parse("green"));

        ctx.Status("Building graft train (local branches)");
        Thread.Sleep(3000);

        ctx.Status("Building graft train (remote branches)");
        Thread.Sleep(3000);

        ctx.Status("Building graft train (remote PRs)");

        // PrintDemoTree();
        Thread.Sleep(3000);
    });

// Now we construct the train on the console
foreach (var branch in branches)
{
    if (branch.IsMerged)
    {
        AnsiConsole.MarkupLine($"- [gray]{branch.Name}[/] [gray](merged)[/]");
        continue;
    }

    var name = branch.Name == currentBranch
        ? $"[green]{branch.Name}[/]"
        : branch.Name;

    AnsiConsole.MarkupLine(
        $"- {name} [gray]({GetBranchStatus(branch.Name)})[/] [blue]({GetRemoteBranchStatus(branch.Name)})[/]");
}

// Get base branch status (as above)
//   fetch origin/base
//   - make note of how many commits it is behind
// Get branch status (as above)
//   for each branch
//     fetch origin/branch
//     - is it a merged branch?
//         - if yes, skip it, but make note (so that the PR tables can be updated)
//     - check that there *is* an origin branch
//         - if there is not, make note of this for later (so we can create one)
//     - check if there are un-pulled commits on the origin
//         - if there are, make note of this for later (so we can pull them)
//     - check if there is a divergent history on the origin
//         - if there is, make note of this for later (so we can ask the user what to do)
//     - check if there is at least one open PR on the origin
//         - if there is, use it as the PR for the branch
//         - if there is a PR for the branch but it is closed, ask the user what to do
//             - save the users response for later
//         - if there are no PRs, make note of this for later (so we can create one later)
//     - check how many commits are on this branch that are not on the next branch
// merge origin/base into local/base (should fast-forward)
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
var city = Prompt.Select($"The PR attached to {currentBranch} has been closed on origin. Would you like to", new[]
{
    "Mark this branch as merged",
    "Create a new PR for this branch",
    "Remove this branch from the train entirely"
});
Console.WriteLine($"Hello, {city}!");

// Mark the "mitchazj-branch-three" branch as merged
var cb = branches.First(x => x.Name == "mitchazj-branch-three");
cb.StoreMergeStatus(!cb.IsMerged, ymlFilePath);


string GetBranchStatus(string branchName)
{
    return "2 commits need grafting";
}

string GetRemoteBranchStatus(string branchName)
{
    return "origin: 2 unpulled commits";
}

string GetBaseBranchStatus()
{
    return "152 commits behind";
}

// Steps:
// 1. Read the github token from ~/.branch/token or (compat with realyse/git-pr-train)
//   - fail and notify the user if it does not exist
// 2. Read the current branch name
// 3. Determine if the branch is a PR branch