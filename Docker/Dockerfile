FROM mono

RUN curl -sL https://deb.nodesource.com/setup_8.x | bash -
RUN apt-get update && apt-get install -y nodejs liblmdb0
RUN /usr/bin/npm config set @bdl:registry https://www.myget.org/F/blockchaindevelopment/npm/
RUN /usr/bin/npm install @bdl/zen-node-yesod@1.0.0 -g

CMD ["zen-node"]


