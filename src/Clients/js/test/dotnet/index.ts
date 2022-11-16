import { CoreIpcServer, NpmProcess } from './CoreIpcServer';
import { BrowserWebSocketAddress } from '../../src/web';
import path from 'path';
import commandLineArgs from 'command-line-args';

async function main(args: string[]): Promise<number> {
    const keyPathNpmPackagejson = 'npm_package_json';
    const pathNpmPackageJson = process.env[keyPathNpmPackagejson];

    if (!pathNpmPackageJson) {
        console.error(
            `Expecting the "${keyPathNpmPackagejson}" environment variable to be set.`
        );
        return 1;
    }

    const pathHome = path.dirname(pathNpmPackageJson);

    const headOptions = commandLineArgs(
        [{ name: 'command', defaultOption: true }],
        { argv: args, stopAtFirstUnknown: true }
    );

    const tailArgs = headOptions._unknown ?? [];

    switch (headOptions.command) {
        case 'host': {
            const keyWebsocketUrl = 'websocket-url';
            const keyScript = 'script';

            const hostOptions = commandLineArgs(
                [
                    {
                        name: keyWebsocketUrl,
                        alias: 'w',
                        type: String,
                    },
                    {
                        name: keyScript,
                        alias: 's',
                        type: String,
                    },
                ],
                { argv: tailArgs }
            );

            const websocketUrl = hostOptions[keyWebsocketUrl] as string;
            const script = hostOptions[keyScript] as string;

            await CoreIpcServer.host(
                new BrowserWebSocketAddress(websocketUrl),
                async () => {
                    await NpmProcess.runAsync(pathHome, script);
                }
            );
        }
        default: {
            return 2;
        }
    }
}

async function bootstrapper() {
    process.exit(await main(process.argv));
}

bootstrapper();
