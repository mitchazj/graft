// See https://aka.ms/new-console-template for more information

using Octokit;
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
using (var reader = new StreamReader(rootPath + "/.pr-train.yml"))
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

// Check whether the current branch is a PR branch
// We can determine this by seeing if the branch name appears in .pr-train.yml which has structure:
// prs:
//   base: master
//   # config
// trains:
//   some train name:
//     - branch1
//     - branch2
//     - branch3
//   some other train name:
//     - cool-changes
//     - more-cool-changes

var trains = (YamlMappingNode)yaml.Documents[0].RootNode["trains"];
var branchNames = trains.Children.Values.SelectMany(x => ((YamlSequenceNode)x).Children).Select(x => x.ToString())
    .ToList();
if (!branchNames.Contains(currentBranch))
{
    AnsiConsole.MarkupLine("[red]Error:[/] The current branch is not a PR branch");
    return;
}

// Get the base branch
var baseBranch = ((YamlMappingNode)yaml.Documents[0].RootNode["prs"])["main-branch-name"].ToString();
if (baseBranch == null)
{
    AnsiConsole.MarkupLine("[red]Error:[/] No base branch specified in .pr-train.yml");
    return;
}

// Print a list of all the branches in this train
var train = trains.Children.First(x => ((YamlSequenceNode)x.Value).Children.Contains(currentBranch));
var trainName = train.Key.ToString();
var trainBranches = ((YamlSequenceNode)train.Value).Children.Select(x => x.ToString()).ToList();

AnsiConsole.MarkupLine($"[gray]{trainName}[/]");
AnsiConsole.MarkupLine($"- [blue]{baseBranch}[/] [gray]({GetBaseBranchStatus()})[/]");
foreach (var branch in trainBranches)
{
    if (branch == currentBranch)
    {
        AnsiConsole.MarkupLine($"- [green]{branch}[/]");
        continue;
    }

    AnsiConsole.MarkupLine($"- {branch} [gray]({GetBranchStatus(branch)})[/] [blue]({GetRemoteBranchStatus(branch)})[/]");
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

void PrintBranches()
{
    foreach (var branch in repo.Branches)
    {
        AnsiConsole.MarkupLine(branch.FriendlyName);
    }
}

void PrintDemoTree()
{
    // Create the tree
    var root = new Tree("Root");

    // Add some nodes
    var foo = root.AddNode("[yellow]Foo[/]");
    var table = foo.AddNode(new Table()
        .RoundedBorder()
        .AddColumn("First")
        .AddColumn("Second")
        .AddRow("1", "2")
        .AddRow("3", "4")
        .AddRow("5", "6"));

    table.AddNode("[blue]Baz[/]");
    foo.AddNode("Qux");

    var bar = root.AddNode("[yellow]Bar[/]");
    bar.AddNode(new Calendar(2020, 12)
        .AddCalendarEvent(2020, 12, 12)
        .HideHeader());

    // Render the tree
    AnsiConsole.Write(root);
}

// Steps:
// 1. Read the github token from ~/.branch/token or (compat with realyse/git-pr-train)
//   - fail and notify the user if it does not exist
// 2. Read the current branch name
// 3. Determine if the branch is a PR branch
// 4. This is a change designed to break things :))