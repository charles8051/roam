ARG BASE_IMAGE=ubuntu:24.04
FROM ${BASE_IMAGE}

ENV DEBIAN_FRONTEND=noninteractive

RUN printf '%s\n' 'Acquire::ForceIPv4 "true";' 'Acquire::Retries "5";' > /etc/apt/apt.conf.d/99roam-network

RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        bash \
        ca-certificates \
        curl \
        git \
        openssh-client \
        openssh-server \
        procps \
        sudo \
        unzip \
    && rm -rf /var/lib/apt/lists/*

RUN useradd --create-home --shell /bin/bash roam \
    && echo "roam ALL=(ALL) NOPASSWD:ALL" > /etc/sudoers.d/roam \
    && chmod 0440 /etc/sudoers.d/roam \
    && mkdir -p /var/run/sshd /home/roam/.ssh /lab-state/source \
    && chown -R roam:roam /home/roam /lab-state \
    && chmod 700 /home/roam/.ssh

COPY scripts/entrypoint.sh /entrypoint.sh

RUN chmod +x /entrypoint.sh

ENTRYPOINT ["/entrypoint.sh"]