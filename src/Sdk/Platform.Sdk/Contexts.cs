// Platform.Sdk — provider-authoring contract surface (§13).
// This file declares the narrow context marker interfaces passed to provider
// stages.  These surfaces are intentionally minimal at Sprint 1 and will be
// expanded additively in Sprint 2 and beyond.
namespace Platform.Sdk;

/// <summary>
/// Provides contextual services available to a provider's binding stage,
/// such as resolution of shared configuration or diagnostic sinks.
/// </summary>
/// <remarks>
/// The members of this interface are frozen for the v1.x engine series;
/// evolution is additive only, via new optional interfaces.
/// Sprint-1 surface: marker only.  Additional members are introduced
/// in Sprint 2.
/// </remarks>
public interface IBindingContext { }

/// <summary>
/// Provides contextual services available to a provider's validation stage,
/// such as access to the project-level configuration, path resolution, and
/// cross-step dependency information.
/// </summary>
/// <remarks>
/// The members of this interface are frozen for the v1.x engine series;
/// evolution is additive only, via new optional interfaces.
/// Sprint-1 surface: marker only.  Additional members are introduced
/// in Sprint 2.
/// </remarks>
public interface IProjectContext { }

/// <summary>
/// Provides contextual services available to a provider's compilation stage,
/// such as the step identifier, the suite-level namespace, and access to
/// shared helper registrations.
/// </summary>
/// <remarks>
/// The members of this interface are frozen for the v1.x engine series;
/// evolution is additive only, via new optional interfaces.
/// Sprint-1 surface: marker only.  Additional members are introduced
/// in Sprint 2.
/// </remarks>
public interface ICompileContext { }
