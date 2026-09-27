"""Session reference identity, independent of provenance and resume capability."""


def session_identity(reference, agent: str) -> tuple[str, str | None, str] | None:
    """Normalize Herdr metadata or a provider reference without inferring persistence.

    Legacy strings have an unknown reference kind. Structured references retain
    their agent, kind and value; reporting fields such as source are not identity.
    """
    if reference is None:
        return None
    if isinstance(reference, str) and reference:
        return agent, None, reference
    if (isinstance(reference, dict) and reference.get("agent") == agent
            and reference.get("kind") in {None, "id", "path"}
            and isinstance(reference.get("value"), str) and reference["value"]):
        return agent, reference.get("kind"), reference["value"]
    raise ValueError("invalid agent session reference")


def same_session(left, right, agent: str) -> bool:
    """Compare known identity fields; an unknown legacy kind may become known."""
    first, second = session_identity(left, agent), session_identity(right, agent)
    if first is None or second is None:
        return first == second
    return (first[0] == second[0] and first[2] == second[2]
            and (first[1] is None or second[1] is None or first[1] == second[1]))
