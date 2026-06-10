"""
Generate Python proto stubs from proto/argus.proto using grpcio-tools.

Run from repo root:
    python detector/scripts/gen_proto.py

Outputs argus_pb2.py, argus_pb2_grpc.py, argus_pb2.pyi into
detector/argus_detector/proto/

Also fixes the grpcio-tools-generated relative import in argus_pb2_grpc.py:
    import argus_pb2  ->  from argus_detector.proto import argus_pb2
"""

import os
import re
import sys
from pathlib import Path

# Resolve repo root (two levels up from detector/scripts/)
SCRIPT_DIR = Path(__file__).resolve().parent
REPO_ROOT = SCRIPT_DIR.parent.parent

PROTO_DIR = REPO_ROOT / "proto"
OUT_DIR = REPO_ROOT / "detector" / "argus_detector" / "proto"
PROTO_FILE = "argus.proto"


def main() -> int:
    if not (PROTO_DIR / PROTO_FILE).exists():
        print(f"ERROR: {PROTO_DIR / PROTO_FILE} not found", file=sys.stderr)
        return 1

    OUT_DIR.mkdir(parents=True, exist_ok=True)

    try:
        from grpc_tools import protoc
    except ImportError:
        print("ERROR: grpcio-tools not installed. Run: pip install grpcio-tools==1.81.0", file=sys.stderr)
        return 1

    # grpc_tools includes well-known-type protos alongside the tool itself
    import grpc_tools
    wkt_root = str(Path(grpc_tools.__file__).parent / "_proto")

    args = [
        "grpc_tools.protoc",
        f"-I{PROTO_DIR}",
        f"-I{wkt_root}",
        f"--python_out={OUT_DIR}",
        f"--grpc_python_out={OUT_DIR}",
        f"--pyi_out={OUT_DIR}",
        str(PROTO_DIR / PROTO_FILE),
    ]

    ret = protoc.main(args)
    if ret != 0:
        print(f"ERROR: protoc exited with code {ret}", file=sys.stderr)
        return ret

    # Fix the generated relative import in argus_pb2_grpc.py.
    # grpcio-tools generates: import argus_pb2 as argus__pb2
    # We need:               from argus_detector.proto import argus_pb2 as argus__pb2
    grpc_file = OUT_DIR / "argus_pb2_grpc.py"
    if grpc_file.exists():
        content = grpc_file.read_text(encoding="utf-8")
        fixed = re.sub(
            r"^import argus_pb2 as argus__pb2",
            "from argus_detector.proto import argus_pb2 as argus__pb2",
            content,
            flags=re.MULTILINE,
        )
        if fixed != content:
            grpc_file.write_text(fixed, encoding="utf-8")
            print("Fixed relative import in argus_pb2_grpc.py")

    print(f"Proto stubs generated in {OUT_DIR}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
