{ pkgs, ... }:

{
  # Every CLI used by `just up` / `just e2e` is pinned here (devenv.lock).
  # The only host prerequisites are Docker and Nix + devenv (docs/04-environment.md).
  languages.dotnet = {
    enable = true;
    package = pkgs.dotnetCorePackages.sdk_10_0;
  };

  packages = with pkgs; [
    just
    kind
    kubectl
    devspace
    # kcat intentionally absent: not cached in devenv-nixpkgs and its libserdes dep fails to build;
    # topics are inspected with kafka-console-consumer.sh inside the Kafka pod instead.
    jq
    valkey        # valkey-cli for `just cache`
    postgresql_17 # psql client for debugging only
  ];

  env = {
    DOTNET_CLI_TELEMETRY_OPTOUT = "1";
    DOTNET_NOLOGO = "1";
    KIND_CLUSTER = "vehicle-cache";
  };

  enterShell = ''
    echo "vehicle-cache dev shell — run 'just' to list recipes"
  '';
}
