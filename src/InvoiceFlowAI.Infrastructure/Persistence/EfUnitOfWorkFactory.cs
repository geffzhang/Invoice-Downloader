// Explicit unit-of-work factory and UoW implementation bound to a single
// InvoiceFlowDbContext instance. The factory produces a UoW that runs
// SQLite BEGIN IMMEDIATE, applies bounded busy timeout (set at the
// connection level during OnConfiguring), and refuses to be used after
// commit or rollback.

using System.Data;
using InvoiceFlowAI.Application.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace InvoiceFlowAI.Infrastructure.Persistence;

public sealed class EfUnitOfWorkFactory : IUnitOfWorkFactory
{
    private readonly InvoiceFlowDbContext _context;

    public EfUnitOfWorkFactory(InvoiceFlowDbContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public async Task<IUnitOfWork> BeginAsync(TransactionPurpose purpose, CancellationToken cancellationToken)
    {
        if (_context.Database.IsRelational())
        {
            var transaction = await _context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
                .ConfigureAwait(false);
            return new EfUnitOfWork(_context, purpose, transaction);
        }

        return new EfUnitOfWork(_context, purpose, transaction: null);
    }
}

internal sealed class EfUnitOfWork : IUnitOfWork
{
    private readonly InvoiceFlowDbContext _context;
    private readonly IDbContextTransaction? _transaction;
    private bool _completed;

    public EfUnitOfWork(InvoiceFlowDbContext context, TransactionPurpose purpose, IDbContextTransaction? transaction)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _transaction = transaction;
        Purpose = purpose;
        TransactionId = Guid.NewGuid().ToString("N");
    }

    public string TransactionId { get; }
    public TransactionPurpose Purpose { get; }
    public bool IsCompleted => _completed;

    public async Task CommitAsync(CancellationToken cancellationToken)
    {
        if (_completed) throw new InvalidOperationException("UoW already completed.");
        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        if (_transaction is not null)
        {
            await _transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        _completed = true;
    }

    public async Task RollbackAsync(CancellationToken cancellationToken)
    {
        if (_completed) return;
        if (_transaction is not null)
        {
            await _transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
        }
        _completed = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (_completed)
        {
            if (_transaction is not null) await _transaction.DisposeAsync().ConfigureAwait(false);
            return;
        }

        if (_transaction is not null)
        {
            try { await _transaction.RollbackAsync().ConfigureAwait(false); }
            catch { /* best effort */ }
            await _transaction.DisposeAsync().ConfigureAwait(false);
        }
        _completed = true;
    }
}