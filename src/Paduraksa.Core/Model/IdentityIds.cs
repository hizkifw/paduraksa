using Paduraksa.Core.Ids;

namespace Paduraksa.Core.Model;

// Typed IDs of the identity model. UserId and ApiTokenId are in ConfigIds.cs because audit actors name them.

/// <summary>
/// An admin session. UuidV4: a session's ID is shown in the session list and handed to the dashboard, so it
/// should not tell when the session began. It is a handle, not a credential: the cookie value is the credential, a
/// separate 256-bit random value of which only the hash is stored.
/// </summary>
[TypedId("ses", IdKind.UuidV4)]
public readonly partial record struct SessionId;
