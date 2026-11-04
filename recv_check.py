"""Unity を使わずに、ネットワーク越しに届いているかを確かめる受信側(Python)。

    python unity_test/recv_check.py --port 5005        # 受信する PC で実行(Unity を止めてから。ポートは同時に使えない)
"""

import argparse
import json
import socket


def main():
    p = argparse.ArgumentParser(description="Print UDP packets from realtime.py / send_dummy.py.")
    p.add_argument("--port", type=int, default=5005)
    p.add_argument("--timeout", type=float, default=0.0, help="この秒数、何も届かなければ終了(0 なら待ち続ける)")
    args = p.parse_args()
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock.bind(("0.0.0.0", args.port))          # 全ネットワークインターフェースで受ける
    sock.settimeout(args.timeout or None)
    print(f"Listening on UDP *:{args.port} ...")
    n = 0
    try:
        while True:
            data, addr = sock.recvfrom(65535)
            d = json.loads(data)
            n += 1
            if n == 1:
                print(f"first packet from {addr[0]}")
            if n % 10 == 0 or d["punch"]:
                print(f"forward={d['forward']} head={d['head']} feet={d['feet']} punch={d['punch']}")
    except (KeyboardInterrupt, socket.timeout):
        pass
    print(f"Received {n} packets.")


if __name__ == "__main__":
    main()
