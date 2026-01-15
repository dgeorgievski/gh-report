# GitHub Repository Inventory Tool - F# Version

This is a high-performance F# implementation of the GitHub repository inventory tool, combining functional programming elegance with .NET runtime performance.

## Why F#?

At the time, this was my only available option which I embraced with a gusto. I love functional programming languages, f# in particular and dotnet core in general.

F# is Microsoft's functional-first language that runs on .NET, making it an **excellent choice for Windows environments**:

- ✅ **Native Windows Integration**: First-class .NET support
- ✅ **Functional Programming**: Immutable by default, pattern matching, type inference
- ✅ **Performance**: JIT-compiled, similar speed to C#
- ✅ **Concurrency**: Built-in async workflows, no thread pool limitations
- ✅ **Type Safety**: Strong static typing with inference
- ✅ **Interoperability**: Can use any .NET library

## Prerequisites

- **.NET 10.0 SDK** or later
- Windows 10/11 (or .NET runtime on Linux/Mac)
- GitHub Personal Access Token with scopes:
  - `repo:all`
  - `read:org`
  - `user:all`
  - `read:enterprise`

## Installation

### 1. Install .NET SDK

```powershell
# Using winget
winget install Microsoft.DotNet.SDK.10

# Or using Chocolatey
choco install dotnet-sdk

# Verify installation
dotnet --version
```

### 2. Build the F# Version

```powershell
cd \gh-migration
.\build.ps1
```

This creates `repos-fsharp.exe` in the current directory.

## Configuration

Set environment variables (same as Python/Go versions):

```powershell
# Required
$env:GITHUB_TOKEN = "your-personal-access-token"

# Optional
$env:GITHUB_SERVER_BASE = "https://github.internal.local"
$env:GITHUB_SSL_CERT = "path/to/cert.pem"                  
```

## Usage

### Basic Commands

```powershell
# Help
.\repos-fsharp.exe --help

# Table output (default)
.\repos-fsharp.exe

# Fetch all organizations (requires admin)
.\repos-fsharp.exe --all-orgs

# CSV output to file
.\repos-fsharp.exe --format csv --output report.csv

# JSON output
.\repos-fsharp.exe --format json -o report.json

# Limit to first N repositories
.\repos-fsharp.exe --count 100 --output limited.csv --format csv
```

### Advanced Examples

```powershell
# Full inventory with all organizations
.\repos-fsharp.exe --all-orgs --fetch-all --format csv --output full-inventory.csv

# Quick sample of first 50 repos
.\repos-fsharp.exe --count 50 --format table

# JSON export for processing
.\repos-fsharp.exe --all-orgs --format json --output repos.json
```

## Command-Line Options

| Flag | Description | Default |
|------|-------------|---------|
| `--format <type>` | Output format: `table`, `json`, or `csv` | `table` |
| `--output <file>`, `-o <file>` | Output file path | stdout |
| `--fetch-all` | Fetch all results without pagination limits | `false` |
| `--all-orgs` | Fetch all organizations (requires admin) | `false` |
| `--count <n>` | Maximum number of repositories to process | unlimited |
| `--help` | Show help message | - |

## Output Columns

Same schema as Python and Go versions:

| Column | Description |
|--------|-------------|
| Organization | GitHub organization name |
| Repository | Repository name |
| Visibility | `private`, `public`, or `internal` |
| Archived | `Yes` or `No` |
| Writer | Collaborators with push access |
| Admin | Collaborators with admin access |
| Languages | Programming languages used |
| Last Accessed | Timestamp of last commit |
| Active PRs | Number of open pull requests |
| 2W PRs | PRs older than 2 weeks |
| 1M PRs | PRs older than 1 month |
