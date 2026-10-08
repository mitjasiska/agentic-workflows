"""Small agent-authored readiness outcome, shared by results and checkpoints.

This validates the delivery contract, never issue quality or formatting.
"""


def validate_assessment(value, implementation_state):
    """Raise ValueError for malformed or contradictory readiness evidence."""
    if (not isinstance(value, dict) or set(value) != {"state", "summary", "questions"}
            or value["state"] not in {"ready", "blocked"}
            or not isinstance(value["summary"], str) or not value["summary"].strip()
            or not isinstance(value["questions"], list)
            or any(not isinstance(q, str) or not q.strip() for q in value["questions"])):
        raise ValueError("invalid task assessment")
    if value["state"] == "blocked":
        if not value["questions"] or implementation_state != "blocked":
            raise ValueError("blocked assessment requires questions and a blocked implementation")
    elif value["questions"]:
        raise ValueError("ready assessment cannot contain blocking questions")
