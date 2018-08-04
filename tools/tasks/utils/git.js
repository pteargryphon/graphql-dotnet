import { exec } from 'shelljs'

export default function git() {
  return new Promise((resolve) => {
    const branch = exec('git ls-remote --heads origin | grep $(git rev-parse HEAD) | cut -d / -f 3', { silent: true }).stdout
    const sha = exec('git rev-parse --short HEAD', { silent: true }).stdout

    resolve({
      branch: (branch || '').trim(),
      sha: (sha || '').trim()
    })
  })
}
