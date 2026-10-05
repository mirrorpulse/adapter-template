"""Test-only memory profile written from the wire specification, without the SDK."""
import argparse
import hashlib
import json
import os
import struct
import tempfile
import uuid


def exact(pipe, count):
    result = bytearray()
    while len(result) < count:
        part = pipe.read(count - len(result))
        if not part:
            raise EOFError()
        result.extend(part)
    return bytes(result)


def receive(pipe):
    count, = struct.unpack('<I', exact(pipe, 4))
    if count > 4194304:
        raise ValueError('FrameTooLarge')
    return exact(pipe, count)


def send_bytes(pipe, data):
    pipe.write(struct.pack('<I', len(data)) + data)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--instance-id', required=True)
    parser.add_argument('--worker-session-id', required=True)
    parser.add_argument('--pipe-name', required=True)
    args = parser.parse_args()
    instance = args.instance_id
    session = args.worker_session_id
    fault = os.environ.get('MP_WIRE_FIXTURE_FAULT', '')
    cache = os.environ['MP_TRANSFER_CACHE_DIR']
    os.makedirs(cache, exist_ok=True)
    capabilities = ['root-addresses', 'stable-operations', 'conditional-targets', 'bounded-streams', 'cancel-ack']
    if fault == 'capability':
        capabilities.remove('cancel-ack')
    with open('\\\\.\\pipe\\' + args.pipe_name, 'r+b', buffering=0) as pipe:
        def send(kind, request, payload, response=True, version=2):
            envelope = dict(protocolVersion=version, messageType=kind, requestId=request,
                            instanceId=instance, workerSessionId=session, isResponse=response, payload=payload)
            send_bytes(pipe, json.dumps(envelope, separators=(',', ':')).encode())

        send('Hello', str(uuid.uuid4()), dict(supportedVersions=dict(minimum=2, maximum=2), capabilities=capabilities), False, 1)
        ready = json.loads(receive(pipe))
        roots = {root['rootKey']: root['enabled'] for root in ready['payload']['roots']}
        files = {(root, 'readme.txt'): ('Memory source: ' + root + '\n').encode() for root in roots}
        directories = set()
        accepted = {}
        uploads = {}

        def revision(root, path):
            data = files.get((root, path))
            return hashlib.sha256(data).hexdigest().upper() if data is not None else 'directory' if (root, path) in directories else None

        send('Connected', str(uuid.uuid4()), {}, False)
        while True:
            raw = receive(pipe)
            if raw.startswith(b'MPB2'):
                root_size, = struct.unpack_from('<H', raw, 4)
                root = raw[6:6 + root_size].decode('utf-8')
                header = raw[6 + root_size:]
                ids = [str(uuid.UUID(bytes_le=header[index:index + 16])) for index in range(0, 64, 16)]
                offset, count, flags = struct.unpack_from('<qIB', header, 64)
                data = header[109:]
                upload = uploads[ids[0]]
                if (root != upload['root'] or ids[1:3] != [instance, session] or ids[3] != upload['stream'] or
                        offset != len(upload['bytes']) or len(data) != count or count > 1048576 or
                        hashlib.sha256(data).digest() != header[77:109] or flags not in (2, 3)):
                    raise ValueError('StreamBindingMismatch')
                upload['bytes'].extend(data)
                with open(upload['lease'], 'ab') as temporary:
                    temporary.write(data)
                if flags == 3:
                    if len(upload['bytes']) != upload['length']:
                        raise ValueError('StreamLengthMismatch')
                    files[(root, upload['path'])] = bytes(upload['bytes'])
                    result = dict(rootKey=root, operationId=upload['operation'], revision=revision(root, upload['path']))
                    accepted[upload['operation']] = (('Upload', root, upload['path'], upload['length'], result['revision']), result)
                    os.unlink(upload['lease'])
                    send('UploadComplete', ids[0], result)
                    del uploads[ids[0]]
                continue
            command = json.loads(raw)
            kind = command['messageType']
            request = command['requestId']
            payload = command['payload']
            if kind == 'Stop':
                return
            root = payload['rootKey']
            if root not in roots or not roots[root]:
                send('OperationError', request, dict(rootKey=root, code='UnknownRoot' if root not in roots else 'RootOffline'))
                continue
            path = payload.get('path', '')
            if kind == 'Stat':
                send('StatResult', request, dict(rootKey=root, revision=revision(root, path)))
            elif kind == 'List':
                data = files[(root, 'readme.txt')]
                entry = dict(remoteId='readme.txt', remoteRevision=revision(root, 'readme.txt'), itemKind='File', relativePath='readme.txt', length=len(data), isDeleted=False)
                send('DirectoryPage', request, dict(rootKey='right' if fault == 'root' else root, entries=[entry], isComplete=True, cursor=None))
            elif kind == 'ReadRange':
                data = files[(root, path)][payload['offset']:payload['offset'] + payload['length']]
                stream = str(uuid.uuid4())
                send('ReadRangeReady', request, dict(rootKey=root, streamId=stream, length=len(data)))
                root_bytes = root.encode()
                header = b''.join(uuid.UUID(value).bytes_le for value in [request, instance, session, stream])
                header += struct.pack('<qIB', payload['offset'], len(data), 3) + hashlib.sha256(data).digest()
                send_bytes(pipe, b'MPB2' + struct.pack('<H', len(root_bytes)) + root_bytes + header + data)
            elif kind == 'Upload':
                descriptor, lease = tempfile.mkstemp(dir=cache)
                os.close(descriptor)
                uploads[request] = dict(root=root, path=path, operation=payload['operationId'], stream=payload['streamId'], length=payload['length'], bytes=bytearray(), lease=lease)
                send('UploadReady', request, dict(rootKey=root, operationId=payload['operationId'], streamId=payload['streamId']))
            elif kind == 'Cancel':
                target = payload['targetRequestId']
                upload = uploads.pop(target)
                if fault == 'cancel':
                    send('CancelAck', request, dict(rootKey=root, targetRequestId=target, status='canceled'))
                    continue
                os.unlink(upload['lease'])
                send('OperationError', target, dict(rootKey=root, operationId=upload['operation'], code='Canceled'))
                send('CancelAck', request, dict(rootKey=root, targetRequestId=target, status='canceled'))
            elif kind in ('Move', 'Delete', 'CreateDirectory'):
                operation = payload['operationId']
                conditions = payload.get('preconditions', {})
                binding = (kind, root, path, payload.get('destinationRootKey'), payload.get('destinationPath'),
                           conditions.get('expectedRevision'), conditions.get('destinationMustBeAbsent', True),
                           payload.get('isDirectory', False), payload.get('mustBeAbsent', True))
                if operation in accepted:
                    previous_binding, previous_result = accepted[operation]
                    if previous_binding != binding:
                        send('OperationError', request, dict(rootKey=root, operationId=operation, code='OperationBindingMismatch'))
                    else:
                        send('MutationComplete', request, previous_result)
                    continue
                if kind == 'Move':
                    files[(payload['destinationRootKey'], payload['destinationPath'])] = files.pop((root, path))
                    result_revision = revision(payload['destinationRootKey'], payload['destinationPath'])
                elif kind == 'Delete':
                    files.pop((root, path))
                    result_revision = None
                else:
                    directories.add((root, path))
                    result_revision = 'directory'
                result = dict(rootKey=root, operationId=operation, revision=result_revision)
                accepted[operation] = (binding, result)
                send('MutationComplete', request, result)
            else:
                raise ValueError('OperationUnsupported')


if __name__ == '__main__':
    main()
