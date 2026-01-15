open System
open System.IO
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading
open System.Threading.Tasks
open System.Collections.Generic
open System.Collections.Concurrent
open FsHttp
open FsHttp.Request
open FsHttp.Response

// Types
type Visibility = Private | Public | Internal

type RepositoryData = {
    Organization: string
    Repository: string
    Visibility: string
    Collaborators: string
    Languages: string
    LastAccessed: string
    ActivePRs: int
    PRs2W: int
    PRs1M: int
}

type Config = {
    Token: string
    ServerBase: string
    SSLCertPath: string option
    OutputFormat: string
    OutputFile: string option
    FetchAll: bool
    AllOrgs: bool
    SearchRepos: bool
    MaxCount: int option
    ExcludedUsers: Set<string>
}

type GitHubOrg = {
    Login: string
    Id: int64
}

type GitHubRepo = {
    Name: string
    Private: bool
    Archived: bool
    Visibility: string option
    Language: string option
}

type GitHubUser = {
    Login: string
    Name: string option
}

type Permissions = {
    Admin: bool
    Maintain: bool
    Push: bool
    Pull: bool
}

type Collaborator = {
    Login: string
    [<JsonPropertyName("ldap_dn")>]
    LdapDn: string option
    [<JsonPropertyName("role_name")>]
    RoleName: string option
    Type: string option  // "User" or "Organization" (teams)
    Permissions: Permissions
}

type Team = {
    Name: string
    Id: int64
    Slug: string
    Permission: string
    Permissions: Permissions
}

type PullRequest = {
    Number: int
    CreatedAt: DateTime
    State: string
}

// Helper function to extract full name from LDAP DN
// Format: "CN=LastName\\, FirstName,OU=..."
// Result: "FirstName LastName[role_name]"
let formatCollaboratorName (collaborator: Collaborator) : string =
    let roleName = collaborator.RoleName |> Option.defaultValue "unknown"
    match collaborator.LdapDn with
    | Some ldapDn ->
        try
            // Extract CN value: "CN=LastName\, FirstName" -> "LastName\, FirstName"
            let cnStart = ldapDn.IndexOf("CN=")
            if cnStart >= 0 then
                let afterCN = ldapDn.Substring(cnStart + 3)
                let cnEnd = afterCN.IndexOf(",OU=")
                let cnValue = if cnEnd > 0 then afterCN.Substring(0, cnEnd) else afterCN
                
                // Parse "LastName\, FirstName" format
                // Note: LDAP DN has escaped comma as "\\,"
                let parts = cnValue.Split([|"\\, "|], StringSplitOptions.None)
                if parts.Length = 2 then
                    let lastName = parts.[0].Trim()
                    let firstName = parts.[1].Trim()
                    $"{firstName} {lastName}[{roleName}]"
                else
                    // Fallback if format doesn't match
                    $"{cnValue}[{roleName}]"
            else
                collaborator.Login
        with
        | _ -> collaborator.Login
    | None -> collaborator.Login

type Commit = {
    Sha: string
    Commit: CommitDetail
}
and CommitDetail = {
    Author: CommitAuthor
}
and CommitAuthor = {
    Date: DateTime option
}

// GitHub API Client
type GitHubClient(token: string, baseUrl: string, ?sslCertPath: string) =
    let baseUrl = baseUrl.TrimEnd('/')
    
    member private _.ParseJson<'T>(json: string) =
        JsonSerializer.Deserialize<'T>(json, JsonSerializerOptions(PropertyNameCaseInsensitive = true))
    
    member this.GetAsync<'T>(endpoint: string) = async {
        try
            let url = $"{baseUrl}/{endpoint.TrimStart('/')}"
            
            let response = 
                http {
                    GET url
                    Authorization $"Bearer {token}"
                    Accept "application/vnd.github+json"
                    header "X-GitHub-Api-Version" "2022-11-28"
                    header "User-Agent" "GitHub-Repo-Inventory-FSharp"
                    config_timeoutInSeconds 30.0
                    config_ignoreCertIssues
                }
                |> Request.send
            
            let content = response |> Response.toText
            let statusCode = response.statusCode
            
            if int response.statusCode >= 200 && int response.statusCode < 300 then
                try
                    return Ok (this.ParseJson<'T> content)
                with parseEx ->
                    return Error $"Parse Error: {parseEx.Message}"
            else
                return Error $"API Error: {statusCode} - {content.Substring(0, min 200 content.Length)}"
        with ex ->
            return Error $"Request failed: {ex.Message}"
    }
    
    member _.GetStringAsync(endpoint: string) = async {
        try
            let url = $"{baseUrl}/{endpoint.TrimStart('/')}"
            
            let response = 
                http {
                    GET url
                    Authorization $"Bearer {token}"
                    Accept "application/vnd.github+json"
                    header "X-GitHub-Api-Version" "2022-11-28"
                    header "User-Agent" "GitHub-Repo-Inventory-FSharp"
                    config_timeoutInSeconds 30.0
                    config_ignoreCertIssues
                }
                |> Request.send
            
            let content = response |> Response.toText
            
            if int response.statusCode >= 200 && int response.statusCode < 300 then
                return Ok content
            else
                return Error $"API Error: {response.statusCode}"
        with ex ->
            return Error $"Request failed: {ex.Message}"
    }

// Utility Functions
module Utils =
    let getEnv key defaultValue =
        match Environment.GetEnvironmentVariable(key) with
        | null | "" -> defaultValue
        | value -> value
    
    let getEnvRequired key =
        match Environment.GetEnvironmentVariable(key) with
        | null | "" -> 
            eprintfn $"Error: {key} environment variable not set"
            exit 1
        | value -> value
    
    let getSSLCertPath() =
        match Environment.GetEnvironmentVariable("GITHUB_SSL_CERT") with
        | null | "" ->
            // Try current directory first, then script directory
            let currentDir = Directory.GetCurrentDirectory()
            let defaultCert1 = Path.Combine(currentDir, "ssl", "IntactUSCA.pem")
            if File.Exists(defaultCert1) then 
                Some defaultCert1
            else
                let scriptDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)
                let defaultCert2 = Path.Combine(scriptDir, "ssl", "IntactUSCA.pem")
                if File.Exists(defaultCert2) then Some defaultCert2 else None
        | cert -> 
            if File.Exists(cert) then Some cert else None
    
    let getVisibility (repo: GitHubRepo) =
        if repo.Private then
            match repo.Visibility with
            | Some "internal" -> "internal"
            | _ -> "private"
        else
            "public"
    
    let yesNo (value: bool) = if value then "Yes" else "No"

// GitHub API Operations
module GitHubAPI =
    open Utils
    
    let fetchOrganizations (client: GitHubClient) (fetchAll: bool) : Async<GitHubOrg list> = async {
        eprintfn "Fetching organizations..."
        
        let rec fetchPages (page: int) (acc: GitHubOrg list) : Async<GitHubOrg list> = async {
            let! result = client.GetAsync<GitHubOrg[]>($"user/orgs")
            
            match result with
            | Ok (orgs: GitHubOrg[]) when orgs.Length > 0 ->
                let newAcc = List.append acc (Array.toList orgs)
                if orgs.Length = 100 && (fetchAll || newAcc.Length < 100) then
                    return! fetchPages (page + 1) newAcc
                else
                    return newAcc
            | Ok _ -> return acc
            | Error err ->
                eprintfn $"Error fetching organizations: {err}"
                return acc
        }
        
        let! orgs = fetchPages 1 []
        
        if not fetchAll && orgs.Length > 100 then
            eprintfn $"Warning: Found {orgs.Length} organizations. Using first 100. Use --fetch-all to get all."
            return orgs |> List.take 100
        else
            return orgs
    }
    
    let fetchAllOrganizations (client: GitHubClient) (fetchAll: bool) : Async<GitHubOrg list> = async {
        eprintfn "Fetching all organizations (requires admin)..."
        
        let rec fetchPages (page: int) (acc: GitHubOrg list) : Async<GitHubOrg list> = async {
            let! result = client.GetAsync<GitHubOrg[]>($"organizations?per_page=100&page={page}")
            
            match result with
            | Ok (orgs: GitHubOrg[]) when orgs.Length > 0 ->
                let newAcc = List.append acc (Array.toList orgs)
                if orgs.Length = 100 && (fetchAll || newAcc.Length < 100) then
                    return! fetchPages (page + 1) newAcc
                else
                    return newAcc
            | Ok _ -> return acc
            | Error err ->
                eprintfn $"Error fetching organizations: {err}"
                return acc
        }
        
        let! orgs = fetchPages 1 []
        
        if not fetchAll && orgs.Length > 100 then
            eprintfn $"Warning: Found {orgs.Length} organizations. Using first 100."
            return orgs |> List.take 100
        else
            return orgs
    }
    
    let fetchRepositories (client: GitHubClient) (orgLogin: string) : Async<GitHubRepo list> = async {
        let rec fetchPages (page: int) (acc: GitHubRepo list) : Async<GitHubRepo list> = async {
            let! result = client.GetAsync<GitHubRepo[]>($"orgs/{orgLogin}/repos?per_page=100&page={page}")
            
            match result with
            | Ok (repos: GitHubRepo[]) when repos.Length > 0 ->
                let newAcc = List.append acc (Array.toList repos)
                if repos.Length = 100 then
                    return! fetchPages (page + 1) newAcc
                else
                    return newAcc
            | Ok _ -> return acc
            | Error _ -> return acc
        }
        
        return! fetchPages 1 []
    }
    
    let fetchLanguages (client: GitHubClient) (owner: string) (repo: string) : Async<string> = async {
        let! result = client.GetStringAsync($"/repos/{owner}/{repo}/languages")
        
        match result with
        | Ok json ->
            try
                use doc = JsonDocument.Parse(json)
                let languages = 
                    doc.RootElement.EnumerateObject()
                    |> Seq.map (fun prop -> prop.Name)
                    |> Seq.toList
                return if languages.IsEmpty then "N/A" else String.Join(", ", languages)
            with _ -> return "N/A"
        | Error _ -> return "N/A"
    }
    
    let fetchTeams (client: GitHubClient) (owner: string) (repo: string) : Async<Team list> = async {
        let! result = client.GetAsync<Team[]>($"repos/{owner}/{repo}/teams")
        
        match result with
        | Ok teams -> return Array.toList teams
        | Error err -> 
            eprintfn $"Error fetching teams for {owner}/{repo}: {err}"
            return! failwith $"Failed to fetch teams: {err}"
    }
    
    let fetchDirectCollaborators (client: GitHubClient) (owner: string) (repo: string) : Async<Collaborator list> = async {
        let! result = client.GetAsync<Collaborator[]>($"repos/{owner}/{repo}/collaborators?affiliation=direct")
        
        match result with
        | Ok collabs -> return Array.toList collabs
        | Error err ->
            eprintfn $"Error fetching direct collaborators for {owner}/{repo}: {err}"
            return! failwith $"Failed to fetch direct collaborators: {err}"
    }
    
    let fetchAllCollaborators (client: GitHubClient) (owner: string) (repo: string) : Async<string> = async {
        // Fetch teams and direct collaborators in parallel
        let! teams = fetchTeams client owner repo
        let! directCollabs = fetchDirectCollaborators client owner repo
        
        // Format teams as "Team Name[permission]"
        let teamStrs = 
            teams
            |> List.map (fun t -> $"{t.Name}[{t.Permission}]")
        
        // Format direct collaborators as "FirstName LastName[role_name]"
        let collabStrs = 
            directCollabs
            |> List.map formatCollaboratorName
        
        // Combine both lists
        let allCollaborators = List.append teamStrs collabStrs
        
        return if allCollaborators.IsEmpty then "N/A" else String.Join(", ", allCollaborators)
    }
    
    let fetchLastCommitDate (client: GitHubClient) (owner: string) (repo: string) : Async<string> = async {
        let! result = client.GetAsync<Commit[]>($"repos/{owner}/{repo}/commits?per_page=1")
        
        match result with
        | Ok (commits: Commit[]) when commits.Length > 0 ->
            match commits.[0].Commit.Author.Date with
            | Some date -> return date.ToString("yyyy-MM-dd HH:mm:ss UTC")
            | None -> return "N/A"
        | _ -> return "N/A"
    }
    
    let fetchPullRequests (client: GitHubClient) (owner: string) (repo: string) : Async<int * int * int> = async {
        let rec fetchPages (page: int) (acc: PullRequest list) : Async<PullRequest list> = async {
            let! result = client.GetAsync<PullRequest[]>($"repos/{owner}/{repo}/pulls?state=open&per_page=100&page={page}")
            
            match result with
            | Ok (prs: PullRequest[]) when prs.Length > 0 ->
                let newAcc = List.append acc (Array.toList prs)
                if prs.Length = 100 then
                    return! fetchPages (page + 1) newAcc
                else
                    return newAcc
            | Ok _ -> return acc
            | Error _ -> return acc
        }
        
        let! prs = fetchPages 1 []
        
        let now = DateTime.UtcNow
        let twoWeeksAgo = now.AddDays(-14.0)
        let oneMonthAgo = now.AddDays(-30.0)
        
        let active = prs.Length
        let twoWeeks = prs |> List.filter (fun pr -> pr.CreatedAt < twoWeeksAgo) |> List.length
        let oneMonth = prs |> List.filter (fun pr -> pr.CreatedAt < oneMonthAgo) |> List.length
        
        return (active, twoWeeks, oneMonth)
    }

// Output Reporter with support for 5 output files based on Last Accessed
type Reporter(format: string, outputFile: string option) =
    let outputLock = obj()
    let mutable isFirstOutput30d = true
    let mutable isFirstOutput60d = true
    let mutable isFirstOutput120d = true
    let mutable isFirstOutput240d = true
    let mutable isFirstOutputGt240d = true
    let mutable fileWriter30d: StreamWriter option = None
    let mutable fileWriter60d: StreamWriter option = None
    let mutable fileWriter120d: StreamWriter option = None
    let mutable fileWriter240d: StreamWriter option = None
    let mutable fileWriterGt240d: StreamWriter option = None
    
    do
        match outputFile with
        | Some filename -> 
            let baseName = Path.GetFileNameWithoutExtension(filename)
            let originalDir = Path.GetDirectoryName(filename)
            let ext = Path.GetExtension(filename)
            
            // Create results directory if it doesn't exist
            let resultsDir = if String.IsNullOrEmpty(originalDir) then "results" else Path.Combine(originalDir, "results")
            if not (Directory.Exists(resultsDir)) then
                Directory.CreateDirectory(resultsDir) |> ignore
            
            let getFilePath suffix = Path.Combine(resultsDir, $"{baseName}{suffix}{ext}")
            
            fileWriter30d <- Some(new StreamWriter(getFilePath "-30d"))
            fileWriter60d <- Some(new StreamWriter(getFilePath "-60d"))
            fileWriter120d <- Some(new StreamWriter(getFilePath "-120d"))
            fileWriter240d <- Some(new StreamWriter(getFilePath "-240d"))
            fileWriterGt240d <- Some(new StreamWriter(getFilePath "-gt-240d"))
        | None -> ()
    
    member private this.ParseLastAccessed(lastAccessed: string) : int option =
        try
            if lastAccessed = "N/A" then None
            else
                // Remove " UTC" suffix if present
                let dateStr = lastAccessed.Replace(" UTC", "")
                let dt = DateTime.Parse(dateStr)
                let daysSince = (DateTime.UtcNow - dt).TotalDays |> int
                Some daysSince
        with _ -> None
    
    member this.ReportBatch(data: RepositoryData list, orgName: string) =
        lock outputLock (fun () ->
            // Split data by Last Accessed into 5 buckets
            let (data30d, data60d, data120d, data240d, dataGt240d) = 
                data
                |> List.fold (fun (acc30, acc60, acc120, acc240, accGt240) item ->
                    match this.ParseLastAccessed(item.LastAccessed) with
                    | Some days when days <= 30 -> (item :: acc30, acc60, acc120, acc240, accGt240)
                    | Some days when days <= 60 -> (acc30, item :: acc60, acc120, acc240, accGt240)
                    | Some days when days <= 120 -> (acc30, acc60, item :: acc120, acc240, accGt240)
                    | Some days when days <= 240 -> (acc30, acc60, acc120, item :: acc240, accGt240)
                    | Some days -> (acc30, acc60, acc120, acc240, item :: accGt240)
                    | None -> (acc30, acc60, acc120, acc240, item :: accGt240)  // Treat N/A as > 240 days
                ) ([], [], [], [], [])
            
            // Output to each file/console
            if not data30d.IsEmpty then
                this.OutputToWriter(fileWriter30d, List.rev data30d, "30d")
            if not data60d.IsEmpty then
                this.OutputToWriter(fileWriter60d, List.rev data60d, "60d")
            if not data120d.IsEmpty then
                this.OutputToWriter(fileWriter120d, List.rev data120d, "120d")
            if not data240d.IsEmpty then
                this.OutputToWriter(fileWriter240d, List.rev data240d, "240d")
            if not dataGt240d.IsEmpty then
                this.OutputToWriter(fileWriterGt240d, List.rev dataGt240d, "gt-240d")
            
            eprintfn $"[Reporter] Processed {data.Length} repositories from {orgName} (30d:{data30d.Length}, 60d:{data60d.Length}, 120d:{data120d.Length}, 240d:{data240d.Length}, >240d:{dataGt240d.Length})"
        )
    
    member private this.OutputToWriter(writer: StreamWriter option, data: RepositoryData list, bucket: string) =
        let targetWriter = 
            match writer with
            | Some w -> w :> TextWriter
            | None -> Console.Out
        
        let originalOut = Console.Out
        if writer.IsSome then
            Console.SetOut(targetWriter)
        
        // Output header only once for CSV per bucket
        if format = "csv" then
            let needsHeader = 
                match bucket with
                | "30d" when isFirstOutput30d -> isFirstOutput30d <- false; true
                | "60d" when isFirstOutput60d -> isFirstOutput60d <- false; true
                | "120d" when isFirstOutput120d -> isFirstOutput120d <- false; true
                | "240d" when isFirstOutput240d -> isFirstOutput240d <- false; true
                | "gt-240d" when isFirstOutputGt240d -> isFirstOutputGt240d <- false; true
                | _ -> false
            
            if needsHeader then
                printfn "organization,repository,visibility,collaborators,languages,last_accessed,active_prs,prs_2w,prs_1m"
        
        // Output the data
        match format with
        | "csv" -> this.OutputCsvRows data
        | "json" -> this.OutputJsonRows data
        | _ -> this.OutputTableRows data
        
        // Flush output
        Console.Out.Flush()
        
        if writer.IsSome then
            Console.SetOut(originalOut)
    
    member private this.OutputCsvRows(data: RepositoryData list) =
        let escape (s: string) =
            if s.Contains(",") || s.Contains("\"") || s.Contains("\n") then
                "\"" + s.Replace("\"", "\"\"") + "\""
            else
                s
        
        for item in data do
            printfn "%s,%s,%s,%s,%s,%s,%d,%d,%d"
                (escape item.Organization)
                (escape item.Repository)
                (escape item.Visibility)
                (escape item.Collaborators)
                (escape item.Languages)
                (escape item.LastAccessed)
                item.ActivePRs
                item.PRs2W
                item.PRs1M
    
    member private this.OutputJsonRows(data: RepositoryData list) =
        // For JSON, we'll just output each record (not ideal for streaming, but works)
        for item in data do
            let options = JsonSerializerOptions(WriteIndented = false)
            let json = JsonSerializer.Serialize(item, options)
            printfn "%s" json
    
    member private this.OutputTableRows(data: RepositoryData list) =
        // For table format, output rows without header (header printed once at start)
        let widths = [| 15; 30; 10; 40; 20; 25; 10; 6; 6 |]
        
        let formatRow (values: string[]) =
            values
            |> Array.mapi (fun i v -> v.PadRight(widths.[i]))
            |> String.concat " | "
            |> sprintf "| %s |"
        
        for item in data do
            let row = [|
                item.Organization
                item.Repository
                item.Visibility
                item.Collaborators
                item.Languages
                item.LastAccessed
                string item.ActivePRs
                string item.PRs2W
                string item.PRs1M
            |]
            printfn "%s" (formatRow row)
    
    member this.Dispose() =
        [ fileWriter30d; fileWriter60d; fileWriter120d; fileWriter240d; fileWriterGt240d ]
        |> List.iter (fun writerOpt ->
            match writerOpt with
            | Some w -> 
                w.Flush()
                w.Close()
                w.Dispose()
            | None -> ())
    
    interface IDisposable with
        member this.Dispose() = this.Dispose()

// Repository Processing
module RepositoryProcessor =
    open GitHubAPI
    open Utils
    
    let processRepository 
        (client: GitHubClient) 
        (orgLogin: string) 
        (repo: GitHubRepo) 
        (config: Config) = async {
        
        printfn $"Processing repository: {orgLogin}/{repo.Name}"
        
        // Run API calls in parallel for better performance
        let! results = 
            Async.Parallel [|
                fetchLanguages client orgLogin repo.Name
                fetchAllCollaborators client orgLogin repo.Name
                fetchLastCommitDate client orgLogin repo.Name
            |]
        
        let languages = results.[0]
        let collaborators = results.[1]
        let lastAccessed = results.[2]
        
        let! (activePRs, prs2W, prs1M) = fetchPullRequests client orgLogin repo.Name
        
        return {
            Organization = orgLogin
            Repository = repo.Name
            Visibility = getVisibility repo
            Collaborators = collaborators
            Languages = languages
            LastAccessed = lastAccessed
            ActivePRs = activePRs
            PRs2W = prs2W
            PRs1M = prs1M
        }
    }
    
    let processOrganization 
        (client: GitHubClient) 
        (org: GitHubOrg) 
        (config: Config) 
        (sharedCounter: int ref)
        (counterLock: obj) 
        (reporter: Reporter) = async {
        
        eprintfn $"[Worker-{org.Login}] Started processing organization: {org.Login}"
        
        let! repos = fetchRepositories client org.Login
        let mutable repoData = []
        let mutable continueProcessing = true
        
        for repo in repos do
            if continueProcessing then
                // Check global count limit
                match config.MaxCount with
                | Some maxCount ->
                    lock counterLock (fun () ->
                        if !sharedCounter >= maxCount then
                            eprintfn $"[Worker-{org.Login}] Global limit of {maxCount} repositories reached. Stopping."
                            continueProcessing <- false
                        else
                            sharedCounter := !sharedCounter + 1
                    )
                | None -> ()
                
                if continueProcessing then
                    let! data = processRepository client org.Login repo config
                    repoData <- data :: repoData
                    
                    // Report in batches of 10
                    if repoData.Length >= 10 then
                        reporter.ReportBatch(List.rev repoData, org.Login)
                        repoData <- []
        
        // Report remaining data
        if not repoData.IsEmpty then
            reporter.ReportBatch(List.rev repoData, org.Login)
        
        eprintfn $"[Worker-{org.Login}] Completed processing organization: {org.Login}"
    }

// Output Formatters
module Output =
    let outputTable (data: RepositoryData list) =
        let headers = [|
            "Organization"; "Repository"; "Visibility"; "Collaborators";
            "Languages"; "Last Accessed"; "Active PRs"; "2W PRs"; "1M PRs"
        |]
        
        // Calculate column widths
        let calculateWidth (selector: RepositoryData -> string) header =
            let dataWidth = 
                if List.isEmpty data then 0
                else data |> List.map selector |> List.map (fun s -> s.Length) |> List.max
            max dataWidth (String.length header)
        
        let widths = [|
            calculateWidth (fun r -> r.Organization) headers.[0]
            calculateWidth (fun r -> r.Repository) headers.[1]
            calculateWidth (fun r -> r.Visibility) headers.[2]
            calculateWidth (fun r -> r.Collaborators) headers.[3]
            calculateWidth (fun r -> r.Languages) headers.[4]
            calculateWidth (fun r -> r.LastAccessed) headers.[5]
            max 10 (String.length headers.[6])
            max 6 (String.length headers.[7])
            max 6 (String.length headers.[8])
        |]
        
        let separator = 
            widths 
            |> Array.map (fun w -> String.replicate (w + 2) "-")
            |> String.concat "+"
            |> sprintf "+%s+"
        
        let formatRow (values: string[]) =
            values
            |> Array.mapi (fun i v -> v.PadRight(widths.[i]))
            |> String.concat " | "
            |> sprintf "| %s |"
        
        printfn "%s" separator
        printfn "%s" (formatRow headers)
        printfn "%s" separator
        
        for item in data do
            let row = [|
                item.Organization
                item.Repository
                item.Visibility
                item.Collaborators
                item.Languages
                item.LastAccessed
                string item.ActivePRs
                string item.PRs2W
                string item.PRs1M
            |]
            printfn "%s" (formatRow row)
        
        printfn "%s" separator
    
    let outputJson (data: RepositoryData list) =
        let options = JsonSerializerOptions(WriteIndented = true)
        let json = JsonSerializer.Serialize(data, options)
        printfn "%s" json
    
    let outputCsv (data: RepositoryData list) =
        printfn "organization,repository,visibility,collaborators,languages,last_accessed,active_prs,prs_2w,prs_1m"
        
        for item in data do
            let escape (s: string) =
                if s.Contains(",") || s.Contains("\"") || s.Contains("\n") then
                    "\"" + s.Replace("\"", "\"\"") + "\""
                else
                    s
            
            printfn "%s,%s,%s,%s,%s,%s,%d,%d,%d"
                (escape item.Organization)
                (escape item.Repository)
                (escape item.Visibility)
                (escape item.Collaborators)
                (escape item.Languages)
                (escape item.LastAccessed)
                item.ActivePRs
                item.PRs2W
                item.PRs1M
    
    let writeToFile (filename: string) (format: string) (data: RepositoryData list) =
        use writer = new StreamWriter(filename)
        let originalOut = Console.Out
        Console.SetOut(writer)
        
        match format with
        | "json" -> outputJson data
        | "csv" -> outputCsv data
        | _ -> outputTable data
        
        Console.SetOut(originalOut)
        eprintfn $"Report saved to: {filename}"

// Main Program
[<EntryPoint>]
let main argv =
    // Parse arguments
    let rec parseArgs args format fetchAll allOrgs searchRepos output count =
        match args with
        | [] -> (format, fetchAll, allOrgs, searchRepos, output, count)
        | "--format" :: fmt :: rest -> parseArgs rest fmt fetchAll allOrgs searchRepos output count
        | "--fetch-all" :: rest -> parseArgs rest format true allOrgs searchRepos output count
        | "--all-orgs" :: rest -> parseArgs rest format fetchAll true searchRepos output count
        | "--search-repos" :: rest -> parseArgs rest format fetchAll allOrgs true output count
        | "--output" :: file :: rest -> parseArgs rest format fetchAll allOrgs searchRepos (Some file) count
        | "-o" :: file :: rest -> parseArgs rest format fetchAll allOrgs searchRepos (Some file) count
        | "--count" :: n :: rest -> 
            match Int32.TryParse(n) with
            | (true, num) when num > 0 -> parseArgs rest format fetchAll allOrgs searchRepos output (Some num)
            | _ -> 
                eprintfn "Error: --count must be a positive integer"
                exit 1
        | "--help" :: _ ->
            printfn "GitHub Repository Inventory Tool (F#)"
            printfn ""
            printfn "Usage: repos [options]"
            printfn ""
            printfn "Options:"
            printfn "  --format <table|json|csv>    Output format (default: table)"
            printfn "  --fetch-all                  Fetch all results without limits"
            printfn "  --all-orgs                   Fetch all organizations (requires admin)"
            printfn "  --search-repos               Search all repositories"
            printfn "  --output, -o <file>          Output file path"
            printfn "  --count <n>                  Maximum number of repositories"
            printfn "  --help                       Show this help"
            exit 0
        | unknown :: _ ->
            eprintfn $"Unknown argument: {unknown}"
            eprintfn "Use --help for usage information"
            exit 1
    
    let (format, fetchAll, allOrgs, searchRepos, outputFile, maxCount) =
        parseArgs (Array.toList argv) "table" false false false None None
    
    // Load configuration
    let config = {
        Token = Utils.getEnvRequired "GITHUB_TOKEN"
        ServerBase = Utils.getEnv "GITHUB_SERVER_BASE" "https://api.github.com"
        SSLCertPath = Utils.getSSLCertPath()
        OutputFormat = format
        OutputFile = outputFile
        FetchAll = fetchAll
        AllOrgs = allOrgs
        SearchRepos = searchRepos
        MaxCount = maxCount
        ExcludedUsers = Set.ofList ["oxramos"; "d1georg"; "ksahluw"]
    }
    
    // Determine base URL
    let baseUrl = 
        if config.ServerBase = "https://api.github.com" then
            config.ServerBase
        else
            if config.ServerBase.EndsWith("/api/v3") then
                config.ServerBase
            else
                config.ServerBase.TrimEnd('/') + "/api/v3"
    
    eprintfn $"Connecting to: {baseUrl}"
    
    let client = new GitHubClient(config.Token, baseUrl, ?sslCertPath = config.SSLCertPath)
    
    // Fetch organizations and process
    let workflow = async {
        let! orgs = 
            if config.AllOrgs then
                GitHubAPI.fetchAllOrganizations client config.FetchAll
            else
                GitHubAPI.fetchOrganizations client config.FetchAll
        
        eprintfn $"Found {orgs.Length} organization(s)."
        
        // Create reporter for immediate output
        use reporter = new Reporter(config.OutputFormat, config.OutputFile)
        
        // Print header for table format
        if config.OutputFormat = "table" && config.OutputFile.IsNone then
            let headers = [|
                "Organization"; "Repository"; "Visibility"; "Collaborators";
                "Languages"; "Last Accessed"; "Active PRs"; "2W PRs"; "1M PRs"
            |]
            let widths = [| 15; 30; 10; 40; 20; 25; 10; 6; 6 |]
            let separator = 
                widths 
                |> Array.map (fun w -> String.replicate (w + 2) "-")
                |> String.concat "+"
                |> sprintf "+%s+"
            let formatRow (values: string[]) =
                values
                |> Array.mapi (fun i v -> v.PadRight(widths.[i]))
                |> String.concat " | "
                |> sprintf "| %s |"
            printfn "%s" separator
            printfn "%s" (formatRow headers)
            printfn "%s" separator
            Console.Out.Flush()
        
        // Process organizations concurrently
        let sharedCounter = ref 0
        let counterLock = obj()
        
        let! _ = 
            orgs
            |> List.map (fun org -> 
                RepositoryProcessor.processOrganization client org config sharedCounter counterLock reporter)
            |> Async.Parallel
        
        // Print footer for table format
        if config.OutputFormat = "table" && config.OutputFile.IsNone then
            let widths = [| 15; 30; 10; 40; 20; 25; 10; 6; 6 |]
            let separator = 
                widths 
                |> Array.map (fun w -> String.replicate (w + 2) "-")
                |> String.concat "+"
                |> sprintf "+%s+"
            printfn "%s" separator
            Console.Out.Flush()
        
        eprintfn "[Main] Processing complete."
    }
    
    Async.RunSynchronously workflow
    0
