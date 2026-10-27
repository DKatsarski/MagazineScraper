using HtmlAgilityPack;
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace ProjectLazarusScraper;

internal sealed record Magazine(string Title, string Slug, Uri Url);
internal sealed record Issue(Magazine Magazine, string Year, string Caption, Uri PageUrl);
internal sealed record ManifestRow(string Magazine, string Year, string Caption, string IssuePage, string PdfUrl, string LocalPath, string Status);

internal static class Program
{
    private const string StartUrl = "https://projectlazarus.bg/magazine-rack/";
    private const int MaxAttempts = 4;
    private static readonly Uri BaseUri = new("https://projectlazarus.bg/");

    // Any absolute or site-relative URL ending in .pdf (optionally followed by a query string).
    private static readonly Regex PdfRegex = new(
        @"(?:https?://|/wp-content/)[^\s""'<>()\\]+?\.pdf(?![a-z0-9])(?:\?[^\s""'<>()\\]*)?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex YearRegex = new(@"\b(?:19|20)\d{2}\b", RegexOptions.Compiled);

    private static readonly HttpClient Http = CreateClient();

    private static string _outputRoot = Path.Combine(Directory.GetCurrentDirectory(), "ProjectLazarus");
    private static bool _dryRun;
    private static int _parallel = 3;
    private static int _delayMs = 500;
    private static string? _onlySlug;

    private static int _downloaded, _skipped, _failed, _noPdf;
    private static readonly ConcurrentBag<ManifestRow> Manifest = new();

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (!ParseArgs(args)) return 1;

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Log("Cancelling… (finishing current requests)");
            cts.Cancel();
        };
        var ct = cts.Token;

        Directory.CreateDirectory(_outputRoot);
        Log($"Output folder: {_outputRoot}{(_dryRun ? "  [DRY RUN – nothing will be downloaded]" : "")}");

        try
        {
            var magazines = await GetMagazinesAsync(ct);
            if (_onlySlug is not null)
                magazines = magazines.Where(m => m.Slug.Equals(_onlySlug, StringComparison.OrdinalIgnoreCase)).ToList();

            Log($"Found {magazines.Count} magazine page(s):");
            foreach (var m in magazines) Log($"  - {m.Title}  ->  {m.Url}");

            foreach (var magazine in magazines)
            {
                ct.ThrowIfCancellationRequested();
                var issues = await GetIssuesAsync(magazine, ct);
                Log($"\n=== {magazine.Title}: {issues.Count} issue(s) across {issues.Select(i => i.Year).Distinct().Count()} year(s) ===");

                await Parallel.ForEachAsync(issues,
                    new ParallelOptions { MaxDegreeOfParallelism = _parallel, CancellationToken = ct },
                    async (issue, token) =>
                    {
                        await ProcessIssueAsync(issue, token);
                        await Task.Delay(_delayMs, token); // be polite to a volunteer-run archive
                    });
            }
        }
        catch (OperationCanceledException)
        {
            Log("Stopped by user. Re-run the program to resume – finished files are skipped.");
        }
        finally
        {
            WriteManifest();
        }

        Log($"\nDone. Downloaded: {_downloaded}, already present: {_skipped}, failed: {_failed}, issues without a PDF link: {_noPdf}");
        if (_noPdf > 0)
            Log($"Pages without a detectable PDF were saved to {Path.Combine(_outputRoot, "_debug")} for inspection.");
        return 0;
    }

    // ---------------------------------------------------------------- step 1: magazine list

    private static async Task<List<Magazine>> GetMagazinesAsync(CancellationToken ct)
    {
        var doc = await LoadDocumentAsync(new Uri(StartUrl), ct);
        var blocks = doc.DocumentNode.SelectNodes(
            "//div[contains(concat(' ', normalize-space(@class), ' '), ' wp-block-media-text ')]");

        var result = new List<Magazine>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (blocks is null) return result;

        foreach (var block in blocks)
        {
            var link = block.SelectSingleNode(".//div[contains(@class,'wp-block-media-text__content')]//a[@href]")
                       ?? block.SelectSingleNode(".//a[@href]");
            if (link is null) continue;

            var url = StripFragment(new Uri(BaseUri, HtmlEntity.DeEntitize(link.GetAttributeValue("href", ""))));
            var title = Clean(link.InnerText);

            // "Games & Music" and "Computer Games" point to the same page (different #anchors) – fetch it once.
            if (!seen.Add(url.AbsoluteUri))
            {
                Log($"  (\"{title}\" shares a page with an earlier entry: {url})");
                continue;
            }

            var slug = url.AbsolutePath.Trim('/');
            if (string.IsNullOrWhiteSpace(slug)) slug = title;
            result.Add(new Magazine(title, SanitizePathPart(slug), url));
        }
        return result;
    }

    // ---------------------------------------------------------------- step 2: issues by year

    private static async Task<List<Issue>> GetIssuesAsync(Magazine magazine, CancellationToken ct)
    {
        var doc = await LoadDocumentAsync(magazine.Url, ct);
        var content = doc.DocumentNode.SelectSingleNode("//div[contains(@class,'entry-content')]") ?? doc.DocumentNode;

        var issues = new List<Issue>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var currentYear = "unknown-year";

        // Walk the content in document order: an <h2>/<h3> with a year sets the current year,
        // and every gallery image with a link after it is an issue of that year.
        foreach (var node in content.Descendants().Where(n => n.NodeType == HtmlNodeType.Element))
        {
            if (node.Name is "h2" or "h3")
            {
                var m = YearRegex.Match(Clean(node.InnerText));
                if (m.Success) currentYear = m.Value;
                continue;
            }

            if (node.Name != "figure" || !node.HasClass("wp-block-image")) continue;

            var a = node.SelectSingleNode(".//a[@href]");
            if (a is null) continue; // filler tiles have no link

            var pageUrl = StripFragment(new Uri(BaseUri, HtmlEntity.DeEntitize(a.GetAttributeValue("href", ""))));
            if (!seen.Add(pageUrl.AbsoluteUri)) continue;

            var caption = Clean(node.SelectSingleNode(".//figcaption")?.InnerText ?? "");
            var year = currentYear;
            if (year == "unknown-year" && YearRegex.Match(caption) is { Success: true } cm) year = cm.Value;

            issues.Add(new Issue(magazine, year, caption, pageUrl));
        }
        return issues;
    }

    // ---------------------------------------------------------------- step 3+4: PDF links & download

    private static async Task ProcessIssueAsync(Issue issue, CancellationToken ct)
    {
        var label = $"{issue.Magazine.Slug}/{issue.Year} – {(issue.Caption.Length > 0 ? issue.Caption : issue.PageUrl.AbsolutePath)}";
        var targetDir = Path.Combine(_outputRoot, issue.Magazine.Slug, issue.Year);

        List<Uri> pdfs;
        string html = "";
        try
        {
            if (IsPdf(issue.PageUrl))
            {
                pdfs = new List<Uri> { issue.PageUrl }; // gallery linked straight to the PDF
            }
            else
            {
                html = await GetStringWithRetryAsync(issue.PageUrl, ct);
                pdfs = ExtractPdfUrls(html);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Interlocked.Increment(ref _failed);
            Log($"[FAIL] {label}: could not load issue page ({ex.Message})");
            Manifest.Add(Row(issue, "", "", "page-error"));
            return;
        }

        if (pdfs.Count == 0)
        {
            Interlocked.Increment(ref _noPdf);
            Log($"[NO PDF] {label}  ({issue.PageUrl})");
            SaveDebugPage(issue, html);
            Manifest.Add(Row(issue, "", "", "no-pdf-found"));
            return;
        }

        foreach (var pdf in pdfs)
        {
            var fileName = SanitizePathPart(Uri.UnescapeDataString(Path.GetFileName(pdf.AbsolutePath)));
            var target = Path.Combine(targetDir, fileName);

            if (_dryRun)
            {
                Log($"[DRY] {label}: {pdf}  ->  {target}");
                Manifest.Add(Row(issue, pdf.AbsoluteUri, target, "dry-run"));
                continue;
            }

            var status = await DownloadAsync(pdf, target, ct);
            Manifest.Add(Row(issue, pdf.AbsoluteUri, target, status));
            Log(status switch
            {
                "downloaded" => $"[OK]   {label}: {fileName}",
                "exists" => $"[SKIP] {label}: {fileName} (already downloaded)",
                _ => $"[FAIL] {label}: {pdf} ({status})"
            });
        }
    }

    private static List<Uri> ExtractPdfUrls(string html)
    {
        // PDF viewers/flipbook plugins hide the file in href/src/data-* attributes, JSON configs
        // (with escaped "\/"), or as an encoded ?file= parameter – normalise all of those first.
        var normalized = WebUtility.HtmlDecode(html.Replace("\\/", "/"));
        string decoded;
        try { decoded = Uri.UnescapeDataString(normalized); } catch { decoded = normalized; }

        var found = new List<Uri>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var text in new[] { normalized, decoded })
            foreach (Match m in PdfRegex.Matches(text))
            {
                var value = m.Value;

                // Cut anything after ".pdf" and, for viewer URLs like ".../viewer.html?file=https://…/x.pdf",
                // keep only the innermost URL.
                var end = value.LastIndexOf(".pdf", StringComparison.OrdinalIgnoreCase);
                if (end >= 0) value = value[..(end + 4)];
                var innerHttp = value.LastIndexOf("http", StringComparison.OrdinalIgnoreCase);
                if (innerHttp > 0) value = value[innerHttp..];
                else
                {
                    var innerRel = value.LastIndexOf("/wp-content/", StringComparison.OrdinalIgnoreCase);
                    var eq = value.LastIndexOf('=');
                    if (eq >= 0 && innerRel > eq) value = value[innerRel..];
                }

                if (!Uri.TryCreate(BaseUri, value, out var uri)) continue;
                if (seen.Add(uri.AbsoluteUri)) found.Add(uri);
            }
        return found;
    }

    private static async Task<string> DownloadAsync(Uri url, string target, CancellationToken ct)
    {
        if (File.Exists(target) && new FileInfo(target).Length > 0)
        {
            Interlocked.Increment(ref _skipped);
            return "exists";
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var tmp = target + ".part";

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using (var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
                {
                    resp.EnsureSuccessStatusCode();
                    await using var src = await resp.Content.ReadAsStreamAsync(ct);
                    await using var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                    await src.CopyToAsync(dst, ct);
                }

                if (!LooksLikePdf(tmp))
                    Log($"[WARN] {url} does not start with %PDF – kept anyway, check it manually.");

                File.Move(tmp, target, overwrite: true);
                Interlocked.Increment(ref _downloaded);
                return "downloaded";
            }
            catch (Exception ex) when (attempt < MaxAttempts && !ct.IsCancellationRequested &&
                                       (ex is HttpRequestException or IOException or TaskCanceledException))
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                TryDelete(tmp);
                Interlocked.Increment(ref _failed);
                return $"error: {ex.Message}";
            }
        }
    }

    // ---------------------------------------------------------------- helpers

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 6
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(15) }; // some PDFs are large
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) ProjectLazarusArchiver/1.0 (personal research backup)");
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("bg,en;q=0.8");
        return client;
    }

    private static async Task<HtmlDocument> LoadDocumentAsync(Uri url, CancellationToken ct)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(await GetStringWithRetryAsync(url, ct));
        return doc;
    }

    private static async Task<string> GetStringWithRetryAsync(Uri url, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await Http.GetStringAsync(url, ct);
            }
            catch (Exception ex) when (attempt < MaxAttempts && !ct.IsCancellationRequested &&
                                       (ex is HttpRequestException or TaskCanceledException))
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct);
            }
        }
    }

    private static bool IsPdf(Uri url) => url.AbsolutePath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikePdf(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> head = stackalloc byte[4];
            return fs.Read(head) == 4 && head[0] == '%' && head[1] == 'P' && head[2] == 'D' && head[3] == 'F';
        }
        catch { return false; }
    }

    private static Uri StripFragment(Uri uri) => new UriBuilder(uri) { Fragment = "" }.Uri;

    private static string Clean(string text) =>
        Regex.Replace(HtmlEntity.DeEntitize(text) ?? "", @"\s+", " ").Trim();

    private static string SanitizePathPart(string name)
    {
        var invalid = new HashSet<char>(Path.GetInvalidFileNameChars().Concat("<>:\"/\\|?*"));
        var sb = new StringBuilder(name.Length);
        foreach (var c in name) sb.Append(invalid.Contains(c) || char.IsControl(c) ? '_' : c);
        var result = sb.ToString().Trim().TrimEnd('.');
        return result.Length == 0 ? "unnamed" : result;
    }

    private static void SaveDebugPage(Issue issue, string html)
    {
        if (string.IsNullOrEmpty(html)) return;
        try
        {
            var dir = Path.Combine(_outputRoot, "_debug");
            Directory.CreateDirectory(dir);
            var name = SanitizePathPart($"{issue.Magazine.Slug}_{issue.PageUrl.AbsolutePath.Trim('/')}.html");
            File.WriteAllText(Path.Combine(dir, name), html, Encoding.UTF8);
        }
        catch { /* debug output is best-effort */ }
    }

    private static ManifestRow Row(Issue i, string pdf, string local, string status) =>
        new(i.Magazine.Title, i.Year, i.Caption, i.PageUrl.AbsoluteUri, pdf, local, status);

    private static void WriteManifest()
    {
        if (Manifest.IsEmpty) return;
        static string Q(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";

        var path = Path.Combine(_outputRoot, "manifest.csv");
        var sb = new StringBuilder("magazine,year,caption,issue_page,pdf_url,local_path,status\n");
        foreach (var r in Manifest.OrderBy(r => r.Magazine).ThenBy(r => r.Year).ThenBy(r => r.IssuePage))
            sb.AppendLine(string.Join(",", Q(r.Magazine), Q(r.Year), Q(r.Caption), Q(r.IssuePage), Q(r.PdfUrl), Q(r.LocalPath), Q(r.Status)));

        // UTF-8 with BOM so Excel shows the Cyrillic captions correctly.
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        Log($"Manifest written to {path}");
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* ignore */ }
    }

    private static readonly object LogLock = new();
    private static void Log(string message)
    {
        lock (LogLock) Console.WriteLine(message);
    }

    private static bool ParseArgs(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--out" when i + 1 < args.Length: _outputRoot = Path.GetFullPath(args[++i]); break;
                case "--parallel" when i + 1 < args.Length && int.TryParse(args[i + 1], out var p): _parallel = Math.Clamp(p, 1, 8); i++; break;
                case "--delay" when i + 1 < args.Length && int.TryParse(args[i + 1], out var d): _delayMs = Math.Max(0, d); i++; break;
                case "--only" when i + 1 < args.Length: _onlySlug = args[++i]; break;
                case "--dry-run": _dryRun = true; break;
                case "-h" or "--help":
                    Console.WriteLine("""
                        Usage: dotnet run -- [options]
                          --out <folder>     where to save (default: ./ProjectLazarus)
                          --only <slug>      one magazine only, e.g. top-games, pc-mania, gamers-workshop
                          --dry-run          list the PDFs that would be downloaded, download nothing
                          --parallel <n>     issue pages processed at once (1–8, default 3)
                          --delay <ms>       pause after each issue (default 500)
                        """);
                    return false;
                default:
                    Console.WriteLine($"Unknown or incomplete option: {args[i]} (use --help)");
                    return false;
            }
        }
        return true;
    }
}