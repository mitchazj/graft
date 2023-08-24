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
var baseBranch = ((YamlMappingNode)yaml.Documents[0].RootNode["prs"])["main-branch-name"].ToString();
if (baseBranch == null)
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
        // todo: better way to interpret the merged flag
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
        // todo: better way to interpret the merged flag
        : new GraftBranch(((YamlMappingNode)x).Children.First().Key.ToString(), true)).ToList();

// Now we construct the train on the console
AnsiConsole.MarkupLine($"[gray]{trainName}[/]");
AnsiConsole.MarkupLine($"- [blue]{baseBranch}[/] [gray]({GetBaseBranchStatus()})[/]");

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

// Synchronous
AnsiConsole.Status()
    .Start("Thinking...", ctx =>
    {
        ctx.Spinner(Spinner.Known.Aesthetic);
        ctx.SpinnerStyle(Style.Parse("green"));

        ctx.Status("Checking local branches");
        Thread.Sleep(3000);

        ctx.Status("Checking remote branches");
        Thread.Sleep(3000);

        ctx.Status("Checking remote PRs");

        // PrintDemoTree();
        Thread.Sleep(3000);
    });

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

// Create a new PR for the "mitchazj-branch-three" branch



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