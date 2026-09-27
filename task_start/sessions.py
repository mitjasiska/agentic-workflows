"""Session reference identity, independent of provenance and resume capability."""

from . import TaskError


class SessionInvalid(TaskError):
    """Positive evidence that retained provider history is missing or inconsistent.

    Ordinary TaskError/OSError failures mean verification could not be completed;
    they do not establish that a previously verified conversation was lost.
    """


def has_immutable_identity(reference, agent: str) -> bool:
    identity = session_identity(reference, agent)
    return bool(identity and (identity[1] == "id" or
                isinstance(reference, dict) and reference.get("conversation_id")))


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
            and isinstance(reference.get("value"), str) and reference["value"]
            and ("conversation_id" not in reference or
                 isinstance(reference["conversation_id"], str) and reference["conversation_id"].strip())):
        return agent, reference.get("kind"), reference["value"]
    raise ValueError("invalid agent session reference")


def same_session(left, right, agent: str) -> bool:
    """Compare known identity fields; an unknown legacy kind may become known."""
    first, second = session_identity(left, agent), session_identity(right, agent)
    if first is None or second is None:
        return first == second
    first_conversation = left.get("conversation_id") if isinstance(left, dict) else None
    second_conversation = right.get("conversation_id") if isinstance(right, dict) else None
    return (first[0] == second[0] and first[2] == second[2]
            and (first[1] is None or second[1] is None or first[1] == second[1])
            and (first_conversation is None or second_conversation is None
                 or first_conversation == second_conversation))


def merge_session(established, reference, agent: str) -> dict | None:
    """Refine existing identity evidence; omissions cannot erase known fields.

    A provider may authenticate an immutable conversation ID behind a path locator.
    It stays in the existing session reference, independently of resumability.
    """
    identity = session_identity(reference, agent)
    if identity is None:
        return established
    if established is not None and not same_session(established, reference, agent):
        raise ValueError("agent session identity changed")
    known = session_identity(established, agent)
    result = dict(agent=identity[0], kind=known[1] if known and known[1] else identity[1], value=identity[2])
    for evidence in (established, reference):
        if isinstance(evidence, dict) and "conversation_id" in evidence:
            result["conversation_id"] = evidence["conversation_id"]
    return result
