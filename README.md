![GRAFT](https://github.com/mitchazj/graft/assets/15032956/e8f90733-72e2-4f6c-b0a5-687b7f1b66d9)

# Graft 🌿
This is a script I wrote to make managing feature branches / merge branches easier.

It's firmly in the `"it doesn't exist" -> "make it PoC"` phase of the
```
"it doesn't exist" -> "make it PoC" -> "make it reliable" -> "make it fast"
```
progressional system.

I was inspired to build this after trying [realyze/pr-train](https://github.com/realyze/pr-train). It works great, but I wanted a fun side-project & also more control over what exactly was happening, so I could do things slightly differently.

---

### How to use:
- make a branch `new-feature-stub`, add it to your `.pr-train.yml` file
- save your github token to `~/.graft/token`
- run `graft`
  - <img src="https://user-images.githubusercontent.com/15032956/263506132-77b6f80a-c1a8-422b-babf-217197168942.png" width="22" /> ooh shiny!
- make a new branch `new-feature-impl` off `new-feature-stub`, add it to your `.pr-train.yml` file after the previous one
- make some changes, commit them
- run `graft` again
  - <img src="https://user-images.githubusercontent.com/15032956/263506132-77b6f80a-c1a8-422b-babf-217197168942.png" width="22" /> ooh a new branch, and it's made a PR train on GitHub!
  - <img src="https://user-images.githubusercontent.com/15032956/263506132-77b6f80a-c1a8-422b-babf-217197168942.png" width="22" /> ooh `master` is behind `origin/master` and it's asked if I wanted to update my entire train
- go back to `new-feature-stub`
- make some changes
- run `graft` *again*
  - <img src="https://user-images.githubusercontent.com/15032956/263506132-77b6f80a-c1a8-422b-babf-217197168942.png" width="22" /> wow it found `new-feature-stub` conflicts with `new-feature-impl` and helped me solve it
  - <img src="https://user-images.githubusercontent.com/15032956/263506132-77b6f80a-c1a8-422b-babf-217197168942.png" width="22" /> wow it updated `new-feature-impl` for me so my Pull Requests are happy
  - <img src="https://user-images.githubusercontent.com/15032956/263506132-77b6f80a-c1a8-422b-babf-217197168942.png" width="22" /> wow it also updated my Pull Requests
- make a new branch `new-feature-cleanup` off `new-feature-impl`
- run `graft` yet again
  - <img src="https://user-images.githubusercontent.com/15032956/263506132-77b6f80a-c1a8-422b-babf-217197168942.png" width="22" /> ooh a coworker pushed changes to `origin/new-feature-impl`
  - <img src="https://user-images.githubusercontent.com/15032956/263506132-77b6f80a-c1a8-422b-babf-217197168942.png" width="22" />  graft already merged those changes into my local `new-feature-impl` for me
  - <img src="https://user-images.githubusercontent.com/15032956/263506132-77b6f80a-c1a8-422b-babf-217197168942.png" width="22" /> wow it also brought `new-feature-cleanup` up-to-date so I don't have to worry about it!
- close the PR for `new-feature-impl` on Github
- run `graft` once more
  - <img src="https://user-images.githubusercontent.com/15032956/263506132-77b6f80a-c1a8-422b-babf-217197168942.png" width="22" /> wow it asked if I want to mark the `new-feature-impl` as merged locally so it doesn't waste time tracking it
  - <img src="https://user-images.githubusercontent.com/15032956/263506132-77b6f80a-c1a8-422b-babf-217197168942.png" width="22" /> wow it updated my PR descriptions to show that `new-feature-impl` merged

### What it will do:
1. Fetch all branches
2. For each branch, merge `origin/{branch}` into `{branch}`
3. Ask if you want to update your first branch with `base` (usually `main` or `master`)
4. Intelligently `graft` (merge) changes in earlier banches through to the tip of your changes
5. For each branch, push `{branch}` to `origin/{branch}` if needed
6. Do its best to maintain a PR train with Table-of-Contents and Merge Status

### Resilience
I've tried to make it gracefully handle hiccups as well as it can. Some example hiccups are:
- merge conflicts
- diverging branches
- the sudden addition of a new branch to the middle of the train
- a surprise close/merge of a PR attached to a branch

if you encounter any issues using this tool, please raise an issue describing;
- the exact situation that went wrong
- what you expected to happen, that didn't
- the steps to reproduce it.

### TODO
- [ ] Automated Tests
- [ ] Code Cleanliness refactor
- [ ] New Features:
  - [ ] cli to generate new train branches
  - [ ] cli to generate PR descriptions, using templates
  - [ ] raycast extension
