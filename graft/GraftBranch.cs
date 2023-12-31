using Octokit;

namespace graft;

enum GraftMergeResult
{
    Error,
    Conflicts,
    Success
}

public class GraftBranch
{
    public string Name { get; set; }

    public bool HasOrigin { get; set; }
    public bool HasDivergedFromOrigin { get; set; }
    
    public int AheadOfOriginBy { get; set; }
    public int BehindOriginBy { get; set; }
    
    public int AheadOfNextBranchBy { get; set; }
    public int BehindNextBranchBy { get; set; }
    
    public bool IsMerged { get; private set; }

    public List<PullRequest> PullRequests { get; set; }

    public GraftBranch(string name, bool isMerged = false)
    {
        Name = name;
        IsMerged = isMerged;
        PullRequests = new List<PullRequest>();
        AheadOfNextBranchBy = 0;
        BehindNextBranchBy = 0;
    }

    public void StoreMergeStatus(bool isMerged, string yamlPath, string baseBranchName)
    {
        if (Name == baseBranchName) return;
        
        IsMerged = isMerged;
        
        // This is a hack implementation to get to MVP. I would be using YamlDotNet
        // to do this, but it doesn't support keeping comments in the file or preserving
        // the original structure of the file.
        
        // Maybe eventually I'll write my own YAML parser that does this, but for now
        // this hack will do.
        
        var lines = File.ReadAllLines(yamlPath);
        for (var i = 0; i < lines.Length; ++i)
        {
            var line = lines[i];
            // Note: this doesn't check if the branch is commented
            if (line.Contains($"- {Name}"))
            {
                if (isMerged)
                {
                    // Note: this doesn't check if the "merged" is commented
                    if (i + 1 < lines.Length && lines[i + 1].Contains("merged:")) return;

                    var indent = line.IndexOf('-');
                    var listLines = lines.ToList();
                    listLines.Insert(i + 1, $"{new string(' ', indent + 2)}  merged: {isMerged.ToString().ToLower()}");
                    
                    // add the colon to the end of the line if it doesn't exist
                    if (!listLines[i].EndsWith(':')) listLines[i] += ":";
                    
                    File.WriteAllLines(yamlPath, listLines);
                    return;
                }
                
                // Note: this doesn't check if the "merged" is commented
                if (lines[i + 1].Contains("merged:"))
                {
                    var listLines = lines.ToList();
                    listLines.RemoveAt(i + 1);
                    if (listLines[i].EndsWith(':')) listLines[i] = listLines[i].Remove(listLines[i].Length - 1);
                    File.WriteAllLines(yamlPath, listLines);
                }

                break;
            }
        }
    }
}