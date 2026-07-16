/* Copyright (C) 2025 Wildfire Games.
 * This file is part of 0 A.D.
 *
 * 0 A.D. is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, either version 2 of the License, or
 * (at your option) any later version.
 *
 * 0 A.D. is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with 0 A.D.  If not, see <http://www.gnu.org/licenses/>.
 */

// This pipeline collects the c++ features available

pipeline {
    agent none

    stages {
        stage('Linux') {
            agent {
                dockerfile {
                    label 'LinuxAgent'
                    customWorkspace 'workspace/featuretest'
                    dir 'build/jenkins/dockerfiles'
                    filename 'debian-12.Dockerfile'
                    // Prevent Jenkins from running commands with the UID of the host's jenkins user
                    // https://stackoverflow.com/a/42822143
                    args '-u root'
                }
            }
            steps {
                sh '''
                    echo Linux GCC Features > featuretestresults.txt
                    echo >> featuretestresults.txt
                    g++-12 --std=c++20 source/tools/featuretest/featuretest.cpp -o featuretest-linux-gcc
                    ./featuretest-linux-gcc >> featuretestresults.txt
                    echo >> featuretestresults.txt

                    echo Linux Clang Features >> featuretestresults.txt
                    echo >> featuretestresults.txt
                    clang++-14 --std=c++20 source/tools/featuretest/featuretest.cpp -o featuretest-linux-clang
                    ./featuretest-linux-clang >> featuretestresults.txt
                    echo >> featuretestresults.txt
                '''
                stash(name: 'results', includes: 'featuretestresults.txt')
            }
        }
        stage('FreeBSD') {
            agent {
                node {
                    label 'FreeBSDAgent'
                    customWorkspace 'workspace/featuretest'
                }
            }
            steps {
                unstash('results')
                sh '''
                    echo FreeBSD Clang Features >> featuretestresults.txt
                    echo >> featuretestresults.txt
                    clang++ --std=c++20 source/tools/featuretest/featuretest.cpp -o featuretest-freebsd-clang
                    ./featuretest-freebsd-clang >> featuretestresults.txt
                    echo >> featuretestresults.txt
                '''
                stash(name: 'results', includes: 'featuretestresults.txt')
            }
        }
        stage('macOS') {
            agent {
                node {
                    label 'macOSAgentVentura'
                    customWorkspace 'workspace/featuretest'
                }
            }
            steps {
                unstash('results')
                sh '''
                    echo macOS AppleClang Features >> featuretestresults.txt
                    echo >> featuretestresults.txt
                    clang++ --std=c++20 source/tools/featuretest/featuretest.cpp -o featuretest-macos-clang
                    ./featuretest-macos-clang >> featuretestresults.txt
                    echo >> featuretestresults.txt
                '''
                stash(name: 'results', includes: 'featuretestresults.txt')
            }
        }
        stage('Windows') {
            agent {
                node {
                    label 'WindowsAgent'
                    customWorkspace 'workspace/featuretest'
                }
            }
            steps {
                unstash('results')
                bat '''
                    ECHO Windows MSVC Features>>featuretestresults.txt
                    ECHO.>>featuretestresults.txt
                    call "C:\\Program Files\\Microsoft Visual Studio\\2022\\Community\\VC\\Auxiliary\\Build\\vcvars64.bat"
                    cl.exe /std:c++20 /Zc:__cplusplus /Fe:featuretest-win.exe source\\tools\\featuretest\\featuretest.cpp /link /SUBSYSTEM:CONSOLE
                    featuretest-win.exe>>featuretestresults.txt
                '''
                stash(name: 'results', includes: 'featuretestresults.txt')
            }
        }
        stage('Aggregate') {
            agent {
                node {
                    label 'macOSAgentVentura'
                    customWorkspace 'workspace/featuretest'
                }
            }
            steps {
                unstash('results')
                sh 'source/tools/featuretest/aggregate.py featuretestresults.txt > featuretestsummary.txt'
                archiveArtifacts(artifacts: 'featuretestresults.txt,featuretestsummary.txt')
                sshPublisher alwaysPublishFromMaster: true, failOnError: true, publishers: [
                    sshPublisherDesc(configName: 'docs.wildfiregames.com', transfers: [
                        sshTransfer(sourceFiles: 'featuretest*.txt', remoteDirectory: 'cppfeatures'),
                    ]
                )]
            }
        }
    }
}
