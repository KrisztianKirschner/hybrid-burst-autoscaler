# Swarm autoscaler controller, on Docker Hardened Images (same policy as the app images)
# Both stages must be the same Python version and Alpine release, or the copied venv won't run.
# Bump deliberately, never between comparison runs.

# Build stage: has pip and a shell
FROM dhi.io/python:3.14-alpine3.24-dev@sha256:df552b9c5634f917614a73c7687a6aebfff432f8ea7a6db754e953e618dc51c1 AS build
WORKDIR /app
RUN python -m venv /app/venv
ENV PATH=/app/venv/bin:$PATH
# Dependencies first: their layer is reused when only controller.py changes
COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

# Runtime stage: no shell, no pip; pinned by digest (python/alpine-3.24/3.14 in the DHI catalog)
FROM dhi.io/python:3.14-alpine3.24@sha256:756ff7001789e8f6db302c9701f7750d359000e11a9ac5e5f6afe26434973e32
WORKDIR /app
COPY --from=build /app/venv /app/venv
COPY controller.py .

# Logs straight to `docker service logs` (no buffering), no .pyc files written at runtime
ENV PATH=/app/venv/bin:$PATH \
    PYTHONUNBUFFERED=1 \
    PYTHONDONTWRITEBYTECODE=1 \
    HBA_POLICY_FILE=/etc/hba-autoscaler/policy.yml

# policy.yml is not baked in: the stack mounts it as a Swarm config at HBA_POLICY_FILE.
# DHI runs as non-root (65532), but the controller scales services through the manager's
# Docker socket, so the stack overrides this with user: "0". Numeric on purpose: the image
# has no "root" entry in /etc/passwd, so user: root fails with "unable to find user root".
EXPOSE 8000
# Exec form: Python is PID 1 and receives Swarm's SIGTERM directly
ENTRYPOINT ["python", "controller.py"]
