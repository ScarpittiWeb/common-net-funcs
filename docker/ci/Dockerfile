ARG UBUNTU_IMAGE=ubuntu:24.04
FROM ${UBUNTU_IMAGE}

ENV DEBIAN_FRONTEND=noninteractive \
	TZ=Etc/UTC

RUN apt-get update \
	&& apt-get install --no-install-recommends --yes \
		ca-certificates \
		curl \
		ffmpeg \
		git \
		libicu74 \
		libkrb5-3 \
		libssl3 \
		tzdata \
		zlib1g \
	&& ln -snf /usr/share/zoneinfo/Etc/UTC /etc/localtime \
	&& echo "${TZ}" > /etc/timezone \
	&& apt-get clean \
	&& rm -rf /var/lib/apt/lists/*
