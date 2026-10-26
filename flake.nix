{
  description = "Entorno de desarrollo .NET";

  inputs = {
    nixpkgs.url = "github:NixOS/nixpkgs/nixos-unstable";
    flake-utils.url = "github:numtide/flake-utils";
  };

  outputs = { self, nixpkgs, flake-utils }:
    flake-utils.lib.eachDefaultSystem (system:
      let
        pkgs = import nixpkgs { inherit system; };

        # Definimos qué versión de .NET queremos.
        # Puedes combinar SDKs y Runtimes si es necesario.
        dotnetSdk = pkgs.dotnetCorePackages.sdk_10_0;
      in
      {
        devShells.default = pkgs.mkShell {
          name = "dotnet-env";

          packages = [
            dotnetSdk
            # Herramientas adicionales útiles
            # pkgs.omnisharp-roslyn # Language server si usas editores como Vim/Emacs
            # pkgs.netcoredbg      # Debugger
            # pkgs.sqlite          # Si usas bases de datos locales
          ];

          # Variables de entorno CRÍTICAS para .NET en NixOS
          env = {
            # Must point at the RUNTIME root (${sdk}/share/dotnet), not the
            # package root: framework-dependent apphosts (e.g. `dotnet tool`
            # binaries like csharp-ls) search $DOTNET_ROOT for host/fxr and
            # fail with "You must install .NET" otherwise. The SDK CLI itself
            # resolves its own location, so this does not affect `dotnet`.
            DOTNET_ROOT = "${dotnetSdk}/share/dotnet";
            
            # .NET suele necesitar librerías de sistema (ICU para globalización, SSL, etc.)
            # que no están en rutas estándar en NixOS. Esto lo soluciona:
            LD_LIBRARY_PATH = pkgs.lib.makeLibraryPath [
              pkgs.stdenv.cc.cc
              pkgs.icu
              pkgs.openssl
              pkgs.zlib
            ];
            
            # Para evitar que dotnet intente recolectar telemetría (opcional)
            DOTNET_CLI_TELEMETRY_OPTOUT = "1";
          };

          shellHook = ''
            echo "Ambiente .NET 10.0 cargado."
            echo "SDK path: $DOTNET_ROOT"
          '';
        };
      }
    );
}