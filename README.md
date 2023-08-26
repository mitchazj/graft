![GRAFT](https://github.com/mitchazj/graft/assets/15032956/e8f90733-72e2-4f6c-b0a5-687b7f1b66d9)

# Graft 🌿
This is a script I wrote to make managing feature branches / merge branches easier.

It's firmly in the `"it doesn't exist" -> "make it PoC"` phase of the
```
"it doesn't exist" -> "make it PoC" -> "make it reliable" -> "make it fast"
```
progressional system.

---

### How to use:
- make a branch, add it to your `.pr-train.yml` file
- save your github token to `~/.graft/token`
- run `graft`
  - ooh, shiny!
- make a new branch off your previous branch, add it to your `.pr-train.yml` file after the previous one
- make some changes, commit them
- run `graft` again
  - ooh, a new branch!
- go back to your original branch
- make some changes
- run `graft` *again*
  - wow, it handles updating the branches above!

### What it will do:
1. Fetch all branches
2. For each branch, merge `origin/{branch}` into `{branch}`
3. Ask if you want to update your first branch with `base` (usually `main` or `master`)
4. Intelligently `graft` (merge) changes towards the tip of your changes
5. For each branch, push `{branch}` to `origin/{branch}` if needed
6. Intelligently maintain a PR train with Table-Of-Contents

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
