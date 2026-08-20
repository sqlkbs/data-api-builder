## Step-by-Step Integration & Rebase Workflow

# Whenever Microsoft updates DAB or you update a custom feature:

### Step A: Keep `main` in Sync, if needed

git fetch upstream
git checkout main
git merge upstream/main


### Step B: Rebase Each Feature Branch onto Clean `main` if did Step A

# Rebase each feature branch individually to make sure it works on top of DAB's latest code. Add more custom feature as needed:

# Update feature 1
git checkout feature/mssql-geometry
git rebase main

# Update feature 2
git checkout feature/mssql-schema-hot-reload
git rebase main


### Step C: Rebuild the Release Branch

# Instead of doing messy ongoing merges on your release branch, the cleanest approach is to **recreate or hard-reset** your release branch from `main` and re-apply your feature branches:

# 1. Reset release branch to match the latest clean main
git checkout release/custom-build
git reset --hard main

# 2. Merge each updated feature branch in sequence
git merge feature/mssql-geometry --no-ff -m "Merge MSSQL Geometry support"
git merge feature/mssql-schema-hot-reload --no-ff -m "Merge MSSQL Schema Hot Reload"

# 3. Push to your private repo to trigger your Azure DevOps / GitHub Actions build
git push origin release/custom-build --force
