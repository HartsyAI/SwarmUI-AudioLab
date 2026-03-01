# AudioLab Docker image for Linux-only engines
# Engines: ACE-Step, RVC, GPT-SoVITS, Resemble-Enhance, CosyVoice, RealtimeSTT
#
# Build:  docker build -t audiolab-linux -f python_backend/docker/Dockerfile .
# Run:    docker compose -f python_backend/docker/docker-compose.yml up -d

FROM nvidia/cuda:12.6.0-devel-ubuntu22.04

ENV DEBIAN_FRONTEND=noninteractive
ENV PYTHONUNBUFFERED=1

# System dependencies
RUN apt-get update && apt-get install -y --no-install-recommends \
    python3.12 python3.12-venv python3.12-dev python3-pip \
    espeak-ng libespeak-ng-dev libsndfile1 ffmpeg git wget curl \
    build-essential cmake pkg-config \
    && rm -rf /var/lib/apt/lists/*

# Set python3.12 as default
RUN update-alternatives --install /usr/bin/python3 python3 /usr/bin/python3.12 1 && \
    update-alternatives --install /usr/bin/python python /usr/bin/python3.12 1

# Create app directory
WORKDIR /app

# Copy python backend code
COPY python_backend/ /app/

# Install base Python dependencies (torch with CUDA)
RUN pip install --no-cache-dir \
    torch==2.6.0+cu126 torchaudio==2.6.0+cu126 \
    --extra-index-url https://download.pytorch.org/whl/cu126

# Install common dependencies
RUN pip install --no-cache-dir \
    numpy>=1.26.0 soundfile>=0.12.0 transformers>=4.40.0 \
    accelerate scipy librosa

# Install Linux-only engine dependencies
# ACE-Step (requires triton which only builds on Linux)
RUN pip install --no-cache-dir triton && \
    pip install --no-cache-dir git+https://github.com/ace-step/ACE-Step.git || true

# Resemble-Enhance (requires deepspeed which only builds on Linux)
RUN pip install --no-cache-dir deepspeed && \
    pip install --no-cache-dir resemble-enhance || true

# RVC dependencies
RUN pip install --no-cache-dir fairseq faiss-cpu praat-parselmouth pyworld || true

# CosyVoice dependencies
RUN pip install --no-cache-dir \
    conformer onnxruntime-gpu hyperpyyaml WeTextProcessing || true

# GPT-SoVITS dependencies
RUN pip install --no-cache-dir \
    cn2an pypinyin g2p_en jieba wordsegment || true

# RealtimeSTT dependencies
RUN pip install --no-cache-dir \
    RealtimeSTT webrtcvad pyaudio || true

# Model storage mount point
VOLUME /models

# Environment variables (overridden by docker-compose)
ENV AUDIOLAB_MODEL_ROOT=/models
ENV HF_HOME=/models/.cache
ENV TRANSFORMERS_CACHE=/models/.cache
ENV HUGGINGFACE_HUB_CACHE=/models/.cache

EXPOSE 18716

ENTRYPOINT ["python", "audio_server.py"]
CMD ["--port", "18716", "--model-root", "/models", "--hf-cache", "/models/.cache"]
