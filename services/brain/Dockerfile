# Brain service — FastAPI (health) + gRPC (quant + referee). ARM-friendly.
FROM ghcr.io/astral-sh/uv:python3.12-bookworm-slim

WORKDIR /app
COPY pyproject.toml uv.lock ./
RUN uv sync --frozen --no-dev

COPY app ./app
COPY gen ./gen

ENV PATH="/app/.venv/bin:$PATH"
ENV PYTHONPATH="/app:/app/gen"
EXPOSE 8081 50061
CMD ["python", "-m", "app.main"]
