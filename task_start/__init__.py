"""Small, standard-library-only task start workflow."""


class TaskError(Exception):
    """An operational failure suitable for displaying without a traceback."""


class HerdrResponseError(TaskError):
    """An unrecognized response envelope, never evidence of an operation's outcome."""


class AgentNotReady(TaskError):
    """Herdr started an agent but could not confirm interactive readiness."""

    def __init__(self, message: str, *, agent: dict | None = None):
        super().__init__(message)
        # A non-ready response can still contain immutable identity evidence.
        self.agent = agent
