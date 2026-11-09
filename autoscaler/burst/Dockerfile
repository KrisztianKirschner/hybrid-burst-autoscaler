# Burst controller, on the same Docker Hardened Images as the replica controller
# Both stages must be the same Python version and Alpine release, or the copied venv won't run.

# Build stage: has pip and a shell
FROM dhi.io/python:3.14-alpine3.24-dev@sha256:df552b9c5634f917614a73c7687a6aebfff432f8ea7a6db754e953e618dc51c1 AS build
WORKDIR /app
RUN python -m venv /app/venv
ENV PATH=/app/venv/bin:$PATH
# Dependencies first: their layer is reused when only the code changes
COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

# Runtime stage: no shell, no pip
FROM dhi.io/python:3.14-alpine3.24@sha256:756ff7001789e8f6db302c9701f7750d359000e11a9ac5e5f6afe26434973e32
WORKDIR /app
COPY --from=build /app/venv /app/venv
COPY controller.py decision.py swarm_adapter.py aws_client.py ./

# Logs straight to `docker service logs` (no buffering), no .pyc files written at runtime
ENV PATH=/app/venv/bin:$PATH \
    PYTHONUNBUFFERED=1 \
    PYTHONDONTWRITEBYTECODE=1 \
    HBA_POLICY_FILE=/etc/hba-burst-controller/policy.yml

# The stack mounts the policy as a Swarm config at HBA_POLICY_FILE, and sets user: "0"
# because draining and removing nodes goes through the manager's Docker socket.
EXPOSE 8000
# Exec form: Python is PID 1 and receives Swarm's SIGTERM directly
ENTRYPOINT ["python", "controller.py"]
