// Pairing engine (design §3 / Task 10). Each document has a role
// (ride-invoice, ride-itinerary, hotel-invoice, hotel-folio) and is
// matched to a companion within a tolerance window. The engine uses
// connected components + optimal assignment so a hub of 3 invoices
// and 2 companions is solved in one pass. Multiple optimal solutions
// in a component surface as PairingAmbiguity and route to manual
// review rather than picking arbitrarily.
//
// The algorithm is the deterministic port of the Python
// pairing_engine.py reference. Tests verify:
//   * Date tolerance (±3 days) for hotels
//   * Ride tax factor (1.03 ± 0.50) tolerance
//   * Provider match
//   * Multiple optimal solutions → ambiguity
//   * Cycle-preserving components (3 invoices + 2 companions)
//   * Empty input → empty result
//   * Unsupported family → ValueException

using InvoiceFlowAI.Domain.Candidates;

namespace InvoiceFlowAI.Application.Pairing;

public enum PairingRole
{
    RideInvoice,
    RideItinerary,
    HotelInvoice,
    HotelFolio,
}

public enum PairingFamily
{
    Ride,
    Hotel,
}

public sealed record PairingDocument(
    string Id,
    PairingRole Role,
    decimal? Amount,
    DateOnly? BusinessDate,
    string Provider,
    IReadOnlyList<string> MerchantTokens,
    string SourceMessageUid,
    string Path);

public sealed record PairingAmbiguity(
    IReadOnlyList<string> DocumentIds,
    string Reason);

public sealed record PairingAssignment(
    PairingDocument Invoice,
    PairingDocument Companion);

public sealed record PairingResult(
    IReadOnlyList<PairingAssignment> Pairs,
    IReadOnlyList<PairingDocument> UnmatchedInvoices,
    IReadOnlyList<PairingDocument> UnmatchedCompanions,
    IReadOnlyList<PairingAmbiguity> Ambiguities);

public interface IPairingEngine
{
    PairingResult Pair(PairingFamily family, IReadOnlyList<PairingDocument> invoices, IReadOnlyList<PairingDocument> companions);
}

public sealed class PairingEngine : IPairingEngine
{
    private static readonly decimal Cent = 0.01m;
    private static readonly decimal RideTaxFactor = 1.03m;
    private static readonly decimal RideTaxSlack = 0.50m;

    public PairingResult Pair(PairingFamily family, IReadOnlyList<PairingDocument> invoices, IReadOnlyList<PairingDocument> companions)
    {
        ArgumentNullException.ThrowIfNull(invoices);
        ArgumentNullException.ThrowIfNull(companions);

        var sortedInvoices = invoices.OrderBy(d => d.Id, StringComparer.Ordinal).ToList();
        var sortedCompanions = companions.OrderBy(d => d.Id, StringComparer.Ordinal).ToList();

        var edges = new Dictionary<Edge, int>();
        for (int i = 0; i < sortedInvoices.Count; i++)
        {
            for (int j = 0; j < sortedCompanions.Count; j++)
            {
                if (Compatible(family, sortedInvoices[i], sortedCompanions[j]))
                {
                    edges[new Edge(i, j)] = EdgeScore(family, sortedInvoices[i], sortedCompanions[j]);
                }
            }
        }

        var acceptedEdges = new HashSet<Edge>();
        var ambiguities = new List<PairingAmbiguity>();

        foreach (var component in ConnectedComponents(sortedInvoices.Count, sortedCompanions.Count, edges))
        {
            var assignments = OptimalAssignments(component.Invoices, component.Companions, edges);
            if (assignments.Count > 1)
            {
                var documentIds = new List<string>();
                foreach (var i in component.Invoices) documentIds.Add(sortedInvoices[i].Id);
                foreach (var j in component.Companions) documentIds.Add(sortedCompanions[j].Id);
                documentIds.Sort(StringComparer.Ordinal);
                ambiguities.Add(new PairingAmbiguity(documentIds, "multiple_optimal_pair_memberships"));
                continue;
            }
            foreach (var pair in assignments[0]) acceptedEdges.Add(pair);
        }

        var pairs = acceptedEdges
            .Select(p => new PairingAssignment(sortedInvoices[p.Invoice], sortedCompanions[p.Companion]))
            .OrderBy(a => a.Invoice.Id, StringComparer.Ordinal)
            .ThenBy(a => a.Companion.Id, StringComparer.Ordinal)
            .ToList();

        var matchedInvoiceIds = pairs.Select(p => p.Invoice.Id).ToHashSet(StringComparer.Ordinal);
        var matchedCompanionIds = pairs.Select(p => p.Companion.Id).ToHashSet(StringComparer.Ordinal);

        return new PairingResult(
            Pairs: pairs,
            UnmatchedInvoices: sortedInvoices.Where(d => !matchedInvoiceIds.Contains(d.Id)).ToList(),
            UnmatchedCompanions: sortedCompanions.Where(d => !matchedCompanionIds.Contains(d.Id)).ToList(),
            Ambiguities: ambiguities
                .OrderBy(a => string.Join(",", a.DocumentIds), StringComparer.Ordinal)
                .ToList());
    }

    private static bool Compatible(PairingFamily family, PairingDocument invoice, PairingDocument companion)
    {
        var invoiceRole = family == PairingFamily.Ride ? PairingRole.RideInvoice : PairingRole.HotelInvoice;
        var companionRole = family == PairingFamily.Ride ? PairingRole.RideItinerary : PairingRole.HotelFolio;
        if (invoice.Role != invoiceRole || companion.Role != companionRole) return false;
        if (invoice.Amount is null || companion.Amount is null) return false;

        var ip = (invoice.Provider ?? "").Trim().ToLowerInvariant();
        var cp = (companion.Provider ?? "").Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(ip) && !string.IsNullOrEmpty(cp) && ip != cp) return false;

        var delta = Math.Abs(invoice.Amount.Value - companion.Amount.Value);
        if (family == PairingFamily.Ride)
        {
            var matches = delta < Cent
                || Math.Abs(invoice.Amount.Value * RideTaxFactor - companion.Amount.Value) < RideTaxSlack
                || Math.Abs(companion.Amount.Value * RideTaxFactor - invoice.Amount.Value) < RideTaxSlack;
            return matches;
        }
        if (delta > Cent) return false;
        if (invoice.BusinessDate is { } ibd && companion.BusinessDate is { } cbd)
        {
            return Math.Abs(ibd.DayNumber - cbd.DayNumber) <= 3;
        }
        return true;
    }

    private static int EdgeScore(PairingFamily family, PairingDocument invoice, PairingDocument companion)
    {
        var score = 0;
        var ip = (invoice.Provider ?? "").Trim().ToLowerInvariant();
        var cp = (companion.Provider ?? "").Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(ip) && ip == cp) score += 100;

        var shared = invoice.MerchantTokens.Intersect(companion.MerchantTokens).Count();
        var total = invoice.MerchantTokens.Union(companion.MerchantTokens).Count();
        if (shared > 0 && total > 0) score += (int)Math.Round(40.0 * shared / total);

        if (!string.IsNullOrEmpty(invoice.SourceMessageUid) && invoice.SourceMessageUid == companion.SourceMessageUid) score += 60;

        if (invoice.Amount is { } ia && companion.Amount is { } ca)
        {
            score += Math.Abs(ia - ca) < Cent ? 30 : 10;
        }

        if (invoice.BusinessDate is { } ibd && companion.BusinessDate is { } cbd)
        {
            var days = Math.Abs(ibd.DayNumber - cbd.DayNumber);
            score += family == PairingFamily.Hotel ? Math.Max(0, 20 - days * 4) : Math.Max(0, 10 - Math.Min(days, 10));
        }
        return score;
    }

    private readonly record struct Edge(int Invoice, int Companion);

    private sealed record Component(List<int> Invoices, List<int> Companions);

    private static List<Component> ConnectedComponents(
        int invoiceCount, int companionCount,
        Dictionary<Edge, int> edges)
    {
        var neighbors = new Dictionary<(string, int), HashSet<(string, int)>>();
        foreach (var edge in edges.Keys)
        {
            var inv = ("invoice", edge.Invoice);
            var cmp = ("companion", edge.Companion);
            if (!neighbors.ContainsKey(inv)) neighbors[inv] = new();
            if (!neighbors.ContainsKey(cmp)) neighbors[cmp] = new();
            neighbors[inv].Add(cmp);
            neighbors[cmp].Add(inv);
        }
        var components = new List<Component>();
        var unseen = new HashSet<(string, int)>(neighbors.Keys);
        while (unseen.Count > 0)
        {
            var pending = new Stack<(string, int)>();
            var seed = unseen.OrderBy(x => x, Comparer<(string, int)>.Create((a, b) =>
            {
                int c = string.Compare(a.Item1, b.Item1, StringComparison.Ordinal);
                return c != 0 ? c : a.Item2.CompareTo(b.Item2);
            })).First();
            pending.Push(seed);
            var componentNodes = new HashSet<(string, int)>();
            while (pending.Count > 0)
            {
                var node = pending.Pop();
                if (!componentNodes.Add(node)) continue;
                unseen.Remove(node);
                foreach (var n in neighbors[node])
                {
                    if (!componentNodes.Contains(n)) pending.Push(n);
                }
            }
            var inv = componentNodes.Where(n => n.Item1 == "invoice").Select(n => n.Item2).OrderBy(x => x).ToList();
            var cmp = componentNodes.Where(n => n.Item1 == "companion").Select(n => n.Item2).OrderBy(x => x).ToList();
            components.Add(new Component(inv, cmp));
        }
        components.Sort((a, b) =>
        {
            int c = ListCompare(a.Invoices, b.Invoices);
            return c != 0 ? c : ListCompare(a.Companions, b.Companions);
        });
        return components;
    }

    private static int ListCompare(List<int> a, List<int> b)
    {
        for (int i = 0; i < Math.Min(a.Count, b.Count); i++)
        {
            if (a[i] != b[i]) return a[i].CompareTo(b[i]);
        }
        return a.Count.CompareTo(b.Count);
    }

    private static List<HashSet<Edge>> OptimalAssignments(
        List<int> invoiceIndices, List<int> companionIndices,
        Dictionary<Edge, int> edges)
    {
        var companionBits = new Dictionary<int, int>();
        for (int i = 0; i < companionIndices.Count; i++) companionBits[companionIndices[i]] = i;

        var memo = new Dictionary<(int, int), ((int pairs, int score), List<HashSet<Edge>>)>();

        ((int, int), List<HashSet<Edge>>) Solve(int position, int usedMask)
        {
            if (memo.TryGetValue((position, usedMask), out var cached)) return cached;
            if (position == invoiceIndices.Count)
            {
                return ((0, 0), new List<HashSet<Edge>> { new() });
            }
            var invoiceIndex = invoiceIndices[position];
            var choices = new List<(int? companion, ((int, int), List<HashSet<Edge>>) child)>
            {
                (null, Solve(position + 1, usedMask)),
            };
            foreach (var companionIndex in companionIndices)
            {
                if (!edges.ContainsKey(new Edge(invoiceIndex, companionIndex))) continue;
                var bit = 1 << companionBits[companionIndex];
                if ((usedMask & bit) != 0) continue;
                choices.Add((companionIndex, Solve(position + 1, usedMask | bit)));
            }

            (int pairs, int score) best = (-1, -1);
            var bestAssignments = new List<HashSet<Edge>>();
            foreach (var (companionIndex, (objective, childAssignments)) in choices)
            {
                var (pairCount, totalScore) = objective;
                if (companionIndex is { } ci)
                {
                    pairCount += 1;
                    totalScore += edges[new Edge(invoiceIndex, ci)];
                }
                var newObjective = (pairCount, totalScore);
                int cmp = newObjective.CompareTo(best);
                if (cmp < 0) continue;
                if (cmp > 0) { best = newObjective; bestAssignments.Clear(); }
                foreach (var assignment in childAssignments)
                {
                    if (bestAssignments.Count >= 2) break;
                    var newAssignment = new HashSet<Edge>(assignment);
                    if (companionIndex is { } ci2) newAssignment.Add(new Edge(invoiceIndex, ci2));
                    if (!bestAssignments.Any(existing => existing.SetEquals(newAssignment)))
                    {
                        bestAssignments.Add(newAssignment);
                    }
                }
            }
            var result = (best, bestAssignments);
            memo[(position, usedMask)] = result;
            return result;
        }

        return Solve(0, 0).Item2;
    }
}
