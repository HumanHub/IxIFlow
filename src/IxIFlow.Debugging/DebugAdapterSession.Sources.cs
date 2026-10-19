using IxIFlow.Core;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;
using Newtonsoft.Json.Linq;

namespace IxIFlow.Debugging;

public sealed partial class DebugAdapterSession
{
    private static BreakpointSource? Location(string? source)
    {
        var colon = source?.LastIndexOf(':') ?? -1;
        return colon > 0 && int.TryParse(source![(colon + 1)..], out var line) && line > 0
            ? new(source![..colon], line) : null;
    }
    private static bool SamePath(string a, string b) => string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);
    private string Remap(string path, bool toBuild)
    {
        path = Normalize(path);
        foreach (var pair in _roots.OrderByDescending(p => (toBuild ? p.Value : p.Key).Length))
        {
            var from = toBuild ? pair.Value : pair.Key;
            var to = toBuild ? pair.Key : pair.Value;
            if (path.Equals(from, StringComparison.OrdinalIgnoreCase) || path.StartsWith(from + "/", StringComparison.OrdinalIgnoreCase))
                return to + path[from.Length..];
        }
        return path;
    }
    private string ToBuildPath(string path) => Remap(path, true);
    private string[] SourcePaths() => _steps.Select(s => Location(s.SourceLocation)?.Path).OfType<string>()
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    private object Source(string path) => new { name = Path.GetFileName(path), path = Remap(path, false),
        sourceReference = Array.FindIndex(SourcePaths(), p => SamePath(p, path)) + 1 };

    private async Task<object> StackTraceAsync(JObject args, CancellationToken token)
    {
        await InspectAsync(token);
        var thread = SelectedThread(args);
        var frames = thread.Frames.Reverse().ToArray();
        var start = Math.Max(0, args.Value<int?>("startFrame") ?? 0);
        var levels = args.Value<int?>("levels") ?? 0;
        if (levels <= 0) levels = 1000;
        return new { stackFrames = frames.Skip(start).Take(Math.Min(levels, 1000)).Select(f =>
        {
            var step = _steps.FirstOrDefault(s => s.CatalogId == $"{f.ScopeId}/{f.NextStepIndex}");
            // At the end of a scope, locate the owning step rather than inventing a location.
            step ??= _steps.Where(s => f.ScopeId.StartsWith(s.CatalogId + "/", StringComparison.Ordinal))
                .OrderByDescending(s => s.CatalogId.Length).FirstOrDefault();
            var location = Location(step?.SourceLocation);
            var id = checked(++_nextId);
            _frames[id] = new(thread.ContinuationId, f, Revision, Generation, Cursor);
            return new { id, name = step?.Name ?? f.Name, line = location == null ? _lineBase : location.Line - 1 + _lineBase,
                column = _columnBase, source = location == null ? null : Source(location.Path),
                presentationHint = location == null ? "subtle" : "normal" };
        }).ToArray(), totalFrames = frames.Length };
    }

    private async Task<object> SetBreakpointsAsync(JObject args, bool functions, CancellationToken token)
    {
        await AuthorizeAsync(DebugOperation.Breakpoints, token);
        var path = functions ? "functions" : ToBuildPath(Required((JObject)args["source"]!, "path"));
        var requests = (args["breakpoints"] as JArray ?? []).OfType<JObject>().ToArray();
        if (requests.Length > 1000) throw new ProtocolException("Too many breakpoints");
        var accepted = new List<Core.Breakpoint>();
        var results = new List<object>();
        foreach (var request in requests)
        {
            var line = (request.Value<int?>("line") ?? _lineBase) + 1 - _lineBase;
            var step = functions
                ? _steps.FirstOrDefault(s => s.CatalogId == request.Value<string>("name") || s.AuthorId == request.Value<string>("name"))
                : _steps.Select(s => (Step: s, Source: Location(s.SourceLocation)))
                    .Where(s => s.Source != null && SamePath(s.Source.Path, path) && s.Source.Line >= line)
                    .OrderBy(s => s.Source!.Line).Select(s => s.Step).FirstOrDefault();
            if (step == null) { results.Add(new { verified = false, message = "No executable step at this address" }); continue; }
            var id = Guid.NewGuid().ToString("N");
            var breakpoint = new Core.Breakpoint(id, step.CatalogId,
                functions ? null : new(path, line), request.Value<string>("hitCondition"),
                Condition: request.Value<string>("condition"));
            accepted.Add(breakpoint);
            results.Add(BreakpointView(id, Location(step.SourceLocation)));
        }
        var all = _breakpointGroups.Where(p => !SamePath(p.Key, path)).SelectMany(p => p.Value).Concat(accepted).ToArray();
        if (_replay == null)
        {
            try
            {
                await AuthorizeAsync(DebugOperation.Breakpoints, token);
                var resolved = await _operations.SetBreakpointsAsync(_instanceId!, all, _endpoint.Actor, token);
                accepted = resolved.Where(b => accepted.Any(a => a.Id == b.Id)).ToList();
            }
            catch (Exception)
            {
                return new { breakpoints = requests.Select(_ => new { verified = false, message = "Breakpoint configuration was refused" }).ToArray() };
            }
        }
        else _replay.SetBreakpoints(all.Select(b => b.StepId));
        _breakpointGroups[path] = accepted;
        foreach (var b in accepted) Emit<BreakpointEvent>(new { reason = "changed", breakpoint = BreakpointView(b.Id, b.VerifiedSource ?? Location(_steps.Single(s => s.CatalogId == b.StepId).SourceLocation)) });
        return new { breakpoints = results };
    }

    private object BreakpointView(string id, BreakpointSource? source) => new { id = BreakpointId(id), verified = true,
        source = source == null ? null : Source(source.Path), line = source == null ? (int?)null : source.Line - 1 + _lineBase };
}
