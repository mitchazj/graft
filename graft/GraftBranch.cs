using Octokit;

namespace graft;

public class GraftBranch
{
    public bool HasLocalChanges { get; set; }
    public bool HasRemoteChanges { get; set; }
    public string Name { get; set; }
    public List<PullRequest> PullRequests { get; set; }
}