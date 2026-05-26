{
  description = "rclsharp dev shell with ROS 2 Humble + .NET 8";

  inputs = {
    nix-ros-overlay.url = "github:lopsided98/nix-ros-overlay/master";
    nixpkgs.follows = "nix-ros-overlay/nixpkgs";
  };

  outputs =
    {
      self,
      nix-ros-overlay,
      nixpkgs,
    }:
    nix-ros-overlay.inputs.flake-utils.lib.eachDefaultSystem (
      system:
      let
        pkgs = import nixpkgs {
          inherit system;
          overlays = [ nix-ros-overlay.overlays.default ];
        };
        rosEnv = pkgs.rosPackages.humble.buildEnv {
          paths = with pkgs.rosPackages.humble; [
            ros-base
            demo-nodes-cpp
            rmw-fastrtps-cpp
          ];
        };
      in
      {
        devShells.default = pkgs.mkShell ({
          name = "rclsharp";
          packages = [
            rosEnv
            pkgs.colcon
            pkgs.dotnet-sdk_8
            pkgs.python3
          ];
          RMW_IMPLEMENTATION = "rmw_fastrtps_cpp";
          DOTNET_CLI_TELEMETRY_OPTOUT = "1";
          DOTNET_NOLOGO = "1";
        } // pkgs.lib.optionalAttrs pkgs.stdenv.isDarwin {
          # Work around Nix dotnet aborting during ICU globalization init on macOS.
          DOTNET_SYSTEM_GLOBALIZATION_INVARIANT = "1";
          # Some ROS 2 Humble binaries in nix-ros-overlay reference libyaml via @rpath.
          DYLD_LIBRARY_PATH = pkgs.lib.makeLibraryPath [ pkgs.libyaml ];
        });
      }
    );

  nixConfig = {
    extra-substituters = [ "https://ros.cachix.org" ];
    extra-trusted-public-keys = [
      "ros.cachix.org-1:dSyZxI8geDCJrwgvCOHDoAfOm5sV1wCPjBkKL+38Rvo="
    ];
  };
}
