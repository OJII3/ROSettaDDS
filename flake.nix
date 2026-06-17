{
  description = "rosettadds dev shell with ROS 2 Humble + .NET 8";

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
        uloop-cli =
          (pkgs.callPackage ./nix/uloop-cli {
            nodejs = pkgs.nodejs;
          })."uloop-cli-2.1.6";
        rosEnv = pkgs.rosPackages.humble.buildEnv {
          paths = with pkgs.rosPackages.humble; [
            ros-base
            demo-nodes-cpp
            rmw-fastrtps-cpp
            # ament_cmakeConfig.cmake requires ament_cmake_core on the
            # CMAKE_PREFIX_PATH when building packages outside the overlay
            # workspace (e.g. `tools/ros2-perf-helper`).
            ament-cmake
            ament-cmake-core
            ament-cmake-auto
            ament-lint-auto
          ];
        };
      in
      {
        devShells.default = pkgs.mkShell (
          {
            name = "rosettadds";
            packages = [
              rosEnv
              pkgs.colcon
              pkgs.dotnet-sdk_8
              pkgs.python3
              uloop-cli
            ];
            RMW_IMPLEMENTATION = "rmw_fastrtps_cpp";
            DOTNET_CLI_TELEMETRY_OPTOUT = "1";
            DOTNET_NOLOGO = "1";
          }
          // pkgs.lib.optionalAttrs pkgs.stdenv.isDarwin {
            # Work around Nix dotnet aborting during ICU globalization init on macOS.
            DOTNET_SYSTEM_GLOBALIZATION_INVARIANT = "1";
            # Some ROS 2 Humble binaries in nix-ros-overlay reference libyaml via @rpath.
            DYLD_LIBRARY_PATH = pkgs.lib.makeLibraryPath [ pkgs.libyaml ];
          }
        );
      }
    );

  nixConfig = {
    extra-substituters = [ "https://ros.cachix.org" ];
    extra-trusted-public-keys = [
      "ros.cachix.org-1:dSyZxI8geDCJrwgvCOHDoAfOm5sV1wCPjBkKL+38Rvo="
    ];
  };
}
