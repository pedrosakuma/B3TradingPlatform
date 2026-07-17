FROM python:3.13-alpine

WORKDIR /app
COPY receiver.py .

USER 65532:65532
EXPOSE 8080
ENTRYPOINT ["python3", "/app/receiver.py"]
