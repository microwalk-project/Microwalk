FROM ghcr.io/microwalk-project/microwalk:jalangi2

# install time
RUN apt-get update -y && apt-get install time -y

# update node
RUN npm install -g n && n 20.10.0

# install dependencies
RUN npm install c8 mustache acorn chalk -g

# set environment variable for chalk to forcefully enable color
ENV FORCE_COLOR='3'
