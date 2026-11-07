using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Paduraksa.Core.Model;

namespace Paduraksa.Data.Storage;

// The model's enums are stored by their stable names (ModelNames), never by number: the names are what the audit
// chain hashes, and they keep the columns readable. Applied to every property of these types by convention.

internal sealed class ActorKindConverter()
    : ValueConverter<ActorKind, string>(value => value.ToName(), name => ModelNames.ParseActorKind(name));

internal sealed class ChannelConverter()
    : ValueConverter<Channel, string>(value => value.ToName(), name => ModelNames.ParseChannel(name));

internal sealed class AuditOutcomeConverter()
    : ValueConverter<AuditOutcome, string>(value => value.ToName(), name => ModelNames.ParseAuditOutcome(name));
